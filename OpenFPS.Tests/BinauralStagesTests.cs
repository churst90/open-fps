using OpenFPS.Client.AudioEngine.Fmod;
using OpenFPS.Client.Core.AudioEngine.SteamAudio;
using ActiveSound = OpenFPS.Client.AudioEngine.Fmod.FmodAudioProvider.ActiveSound;
using Answer = OpenFPS.Client.AudioEngine.Fmod.BinauralStages.Answer;

namespace OpenFPS.Tests;

/// <summary>
/// The binaural pool by the one rank (BinauralStages, docs/CLIENT_NOTES.md "The binaural pool"). On Main Street in
/// traffic and rain (Cody's log, 2026-10-17; AudioLab --busy-street) the pool was first come, first served: other
/// people's footsteps 140 to 180 m off held 25 to 70 of its 160 stages, it sat at 0 to 20 free, and a horn 9 dB under
/// the loudest voice, a raindrop at your feet, birds and nearer steps were refused and never played; a reverb bus made
/// meanwhile went mono. Here the pool is run as the provider runs it, a street's worth of voices asking for stages
/// every update, and what it gives is checked against the voices' own levels.
/// </summary>
public class BinauralStagesTests
{
    private const double Dt = 0.004;
    /// <summary>A stage let go comes back four mixer blocks after its voice stops (FmodAudioProvider.ReleaseAfterEndSeconds).</summary>
    private static readonly double Retire = FmodAudioProvider.ReleaseAfterEndSeconds;

    private static float Gain(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>The pool as the provider runs it: stages, reverb heads, voices playing and voices retiring.</summary>
    private sealed class Pool
    {
        public int Free = FmodAudioProvider.SaPoolSize;
        public readonly List<ActiveSound> Sounds = new();
        private readonly List<(ActiveSound A, double Ends)> _lives = new();
        private readonly List<double> _retiring = new();
        private readonly List<ActiveSound> _leavers = new();
        private readonly Dictionary<ActiveSound, double> _leftAt = new();
        public float Loudest, Floor;
        public double Now;
        public int Heads, Short, Outranked, Unheard, Given, LetGo, LeastFree = int.MaxValue, HeadsRefused;
        public double FreeSum; public int Updates;
        /// <summary>Refusals the oracle finds wrong: a voice refused while louder than one keeping its stage, or
        /// refused as unheard while within the depth of the loudest.</summary>
        public readonly List<string> Wrong = new();
        private int _id;

        /// <summary>A reverb bus's head, taken before any voice (leave 0).</summary>
        public bool TakeHead()
        {
            if (Free <= 0) { HeadsRefused++; return false; }
            Free--; Heads++;
            return true;
        }

        public void GiveHeadBack() { Free++; Heads--; }

        /// <summary>A voice asking for a stage, as PlaySpatialSound asks.</summary>
        public Answer Ask(float level, double seconds, bool oneShot, bool essential = false)
        {
            var answer = BinauralStages.Ask(Free, essential, false, oneShot, level, Loudest, Floor);
            switch (answer)
            {
                case Answer.Short: Short++; return answer;
                case Answer.Unheard:
                    Unheard++;
                    if (level >= Loudest * BinauralStages.UnheardGain) Wrong.Add($"unheard at {Db(level)} with the loudest at {Db(Loudest)}");
                    return answer;
                case Answer.Outranked:
                    Outranked++;
                    // The oracle: none keeping a stage may be quieter than this one by more than the hysteresis.
                    foreach (var a in Sounds)
                        if (a.SaState != null && !a.Leaving && !a.Essential && a.Heard * BinauralStages.Hysteresis < level)
                        { Wrong.Add($"outranked at {Db(level)} while {Db(a.Heard)} keeps a stage"); break; }
                    return answer;
            }
            int leave = essential ? 0 : BinauralStages.HeadReserve;
            if (Free <= leave) { Short++; return Answer.Short; }
            Free--; Given++;
            var s = new ActiveSound { EntityId = ++_id, Heard = level, Essential = essential, OneShot = oneShot, SaState = new SteamAudioVoiceState() };
            Sounds.Add(s);
            _lives.Add((s, oneShot ? Now + seconds : double.MaxValue));
            return Answer.Given;
        }

        /// <summary>One update: the provider's RankStages, then its pass over the voices (a voice let go is stopped by
        /// the mixer its fade and two blocks later, a one-shot at its end; the stage back four blocks after), then the
        /// clock.</summary>
        public void Update()
        {
            for (int i = _retiring.Count - 1; i >= 0; i--)
                if (Now - _retiring[i] >= Retire) { Free++; _retiring.RemoveAt(i); }
            Loudest = BinauralStages.Loudest(Sounds);
            Floor = BinauralStages.Floor(Sounds, Free);
            BinauralStages.ChooseLeavers(Sounds, Free, _leavers);
            foreach (var a in _leavers) { a.Leaving = true; _leftAt[a] = Now; }
            for (int i = _lives.Count - 1; i >= 0; i--)
            {
                var (a, ends) = _lives[i];
                bool stop = Now >= ends;
                if (!stop && a.Leaving && Now - _leftAt[a] >= FmodAudioProvider.LeaveSeconds) { stop = true; LetGo++; }
                if (stop) { Sounds.Remove(a); _lives.RemoveAt(i); _retiring.Add(Now); _leftAt.Remove(a); }
            }
            LeastFree = Math.Min(LeastFree, Free);
            FreeSum += Free; Updates++;
            Now += Dt;
        }

        private static string Db(float g) => $"{20f * MathF.Log10(MathF.Max(g, 1e-12f)):F0} dB";
    }

    /// <summary>
    /// The street as measured: forty engines and sixteen machines at steady levels, sixteen rain patches, birds calling,
    /// twelve drops a second at your feet, a horn now and then, and three hundred and fifty walkers' steps a second from
    /// 3 to 300 m, most of them 30 to 60 dB under the loudest car. Four reverb buses heard, each with its head, one of
    /// them made afresh every two seconds as you walk.
    /// </summary>
    private static Pool Street(int seed, double seconds = 20)
    {
        var rng = new Random(seed);
        var pool = new Pool();
        for (int i = 0; i < 4; i++) pool.TakeHead();
        float Level(float lo, float hi) => Gain(lo + (float)rng.NextDouble() * (hi - lo));
        for (int i = 0; i < 40; i++) pool.Ask(Level(-40f, 0f), 0, oneShot: false);
        for (int i = 0; i < 16; i++) pool.Ask(Level(-45f, -20f), 0, oneShot: false);
        for (int i = 0; i < 16; i++) pool.Ask(Level(-25f, -3f), 0, oneShot: false);
        double nextBus = 2.0;
        while (pool.Now < seconds)
        {
            pool.Update();
            // Steps: a walker's step heard 3 to 300 m off, 0.4 s long.
            for (int k = 0; k < Poisson(rng, 350 * Dt); k++)
            {
                float metres = 3f * MathF.Pow(100f, (float)rng.NextDouble());
                pool.Ask(Gain(-18f) / metres, 0.4, oneShot: true);
            }
            for (int k = 0; k < Poisson(rng, 12 * Dt); k++) pool.Ask(Level(-25f, -10f), 0.15, oneShot: true);
            for (int k = 0; k < Poisson(rng, 5 * Dt); k++) pool.Ask(Level(-35f, -15f), 0.8, oneShot: true);
            for (int k = 0; k < Poisson(rng, 0.5 * Dt); k++) pool.Ask(Level(-12f, -3f), 0.6, oneShot: true);
            for (int k = 0; k < Poisson(rng, 2 * Dt); k++) pool.Ask(Gain(-6f), 0.3, oneShot: true, essential: true);
            if (pool.Now >= nextBus)
            {
                nextBus += 2.0;
                pool.GiveHeadBack();
                pool.TakeHead();
            }
        }
        return pool;
    }

    private static int Poisson(Random rng, double mean)
    {
        int n = 0; double p = Math.Exp(-mean), s = p, u = rng.NextDouble();
        while (u > s && n < 50) { n++; p *= mean / n; s += p; }
        return n;
    }

    /// <summary>
    /// The pool never refuses a voice louder than one keeping its stage, and never runs short: every voice not started
    /// was either quieter than all those holding a stage or more than 40 dB under the loudest voice playing. First come,
    /// first served (the pool as it was), the street refused hundreds a second, the horn among them.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_voice_louder_than_one_holding_a_stage_is_never_refused(int seed)
    {
        var pool = Street(seed);
        Assert.Equal(0, pool.Short);
        Assert.Empty(pool.Wrong);
        // The street does press on the pool: the rank decided, not luck.
        Assert.True(pool.Unheard > 1000, $"{pool.Unheard} unheard");
        Assert.True(pool.Given > 1000, $"{pool.Given} given");
    }

    /// <summary>A reverb bus's head is taken before any voice: on the busy street every bus heard, and every one made
    /// as you walk, has its head (its tail placed binaurally rather than folded to one channel, 134bfa85).</summary>
    [Fact]
    public void A_reverb_bus_heard_always_has_its_head()
    {
        var pool = Street(4);
        Assert.Equal(0, pool.HeadsRefused);
        Assert.Equal(4, pool.Heads);
        Assert.True(pool.LeastFree >= BinauralStages.HeadReserve - 1, $"least free {pool.LeastFree}");
    }

    /// <summary>A dense scene leaves the pool headroom: about the headroom free on average, so a voice arriving between
    /// two updates finds a stage, and never less than the heads' reserve.</summary>
    [Fact]
    public void A_dense_street_leaves_the_pool_headroom()
    {
        var pool = Street(5);
        double mean = pool.FreeSum / pool.Updates;
        Assert.True(mean >= BinauralStages.HeadReserve + BinauralStages.ArrivalHeadroom / 2, $"mean free {mean:F1}");
        Assert.True(pool.LeastFree >= BinauralStages.HeadReserve, $"least free {pool.LeastFree}");
    }

    /// <summary>More voices loud enough to hear than there are stages: the quietest are let go (faded and stopped) for
    /// louder ones, and what keeps a stage is always the loudest.</summary>
    [Fact]
    public void Past_the_pool_the_quietest_are_let_go_for_louder_ones()
    {
        var rng = new Random(6);
        var pool = new Pool();
        for (int i = 0; i < 4; i++) pool.TakeHead();
        // Two hundred continuous voices within 20 dB of each other, more than the pool holds.
        for (int i = 0; i < 200; i++) { pool.Ask(Gain(-20f * (float)rng.NextDouble()), 0, oneShot: false); for (int k = 0; k < 3; k++) pool.Update(); }
        for (int k = 0; k < 200; k++) pool.Update();
        Assert.True(pool.LetGo > 0, "nothing let go");
        Assert.Empty(pool.Wrong);
        Assert.Equal(0, pool.Short);
        float quietestKept = pool.Sounds.Min(a => a.Heard);
        Assert.True(pool.Free >= BinauralStages.HeadReserve + BinauralStages.ArrivalHeadroom - 1, $"free {pool.Free}");
        // A voice louder than the quietest kept by more than the hysteresis gets a stage.
        Assert.Equal(Answer.Given, pool.Ask(quietestKept * 2f, 0, oneShot: false));
    }

    /// <summary>
    /// A one-shot far under the loudest voice playing is not heard against it and is not started; a continuous voice is
    /// not judged so (it is ranked, and let go only for a louder one); your own steps and speech always play; a reflection
    /// leaves the headroom to the direct sounds and plays panned without a stage.
    /// </summary>
    [Fact]
    public void Who_asks_decides_what_the_rank_may_refuse()
    {
        float loudest = 1f, quiet = Gain(-45f);
        Assert.Equal(Answer.Unheard, BinauralStages.Ask(150, false, false, oneShot: true, quiet, loudest, 0f));
        Assert.Equal(Answer.Given, BinauralStages.Ask(150, false, false, oneShot: true, Gain(-35f), loudest, 0f));
        Assert.Equal(Answer.Given, BinauralStages.Ask(150, false, false, oneShot: false, quiet, loudest, 0f));
        Assert.Equal(Answer.Given, BinauralStages.Ask(1, essential: true, false, oneShot: true, quiet, loudest, 1f));
        Assert.Equal(Answer.Panned, BinauralStages.Ask(0, essential: true, false, oneShot: true, quiet, loudest, 1f));
        Assert.Equal(Answer.Panned, BinauralStages.Ask(BinauralStages.ReflectionLeaves, false, reflection: true, false, 1f, loudest, 0f));
        Assert.Equal(Answer.Given, BinauralStages.Ask(BinauralStages.ReflectionLeaves + 1, false, reflection: true, false, 1f, loudest, 0f));
        // Short of the heads' reserve, a voice louder than the floor is a shortage; one quieter is outranked.
        Assert.Equal(Answer.Short, BinauralStages.Ask(BinauralStages.HeadReserve, false, false, false, 0.5f, loudest, 0.1f));
        Assert.Equal(Answer.Outranked, BinauralStages.Ask(BinauralStages.HeadReserve, false, false, false, 0.11f, loudest, 0.1f));
        Assert.Equal(Answer.Outranked, BinauralStages.Ask(20, false, false, false, 0.11f, loudest, 0.1f));
    }

    /// <summary>A reverb bus keeps its head while it is one of the rooms heard or still above silence, and gives it back
    /// once it is neither: on the street twenty-four buses each held one while four at most were heard.</summary>
    [Fact]
    public void A_silent_bus_not_heard_gives_its_head_back()
    {
        Assert.True(FmodAudioProvider.KeepsHead(candidate: true, volume: 0f));
        Assert.True(FmodAudioProvider.KeepsHead(candidate: false, volume: 0.01f));
        Assert.False(FmodAudioProvider.KeepsHead(candidate: false, volume: FmodAudioProvider.HeadSilentVolume * 0.5f));
    }
}

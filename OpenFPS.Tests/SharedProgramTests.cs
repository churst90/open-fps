using System.Numerics;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using OpenFPS.Client.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Sound;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Speakers sharing a program keep one track clock (SharedPrograms; Resonance's 3.3, reproduced on open-fps
/// 2026-10-17). Each start the mixer is asked for is read as the mixer reads it (SharedPrograms.Join): where in its
/// sound it starts and after how long, and so which moment of the program left the speaker when what you hear
/// left it. Every speaker of one program must agree on that, each heard after its own flight.
/// </summary>
public class SharedProgramTests
{
    private readonly ITestOutputHelper _o;
    public SharedProgramTests(ITestOutputHelper o) => _o = o;

    /// <summary>A world time well into the year, as the game's is; the harness's own clock starts at 1.</summary>
    private const double WorldAt = 2.5e7;
    private const int Rate = 48000;

    /// <summary>A program of <paramref name="seconds"/>: speech-band noise, so the loudspeaker's chain has
    /// something to render.</summary>
    private static float[] Program(double seconds)
    {
        var rng = new Random(3);
        var pcm = new float[(int)(seconds * Rate)];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(rng.NextDouble() * 2.0 - 1.0) * 0.3f;
        return pcm;
    }

    private static void AddSpeaker(ClientAudioHarness h, int id, Vector3 at, Quaternion facing, string program, string speaker,
                                   PlaybackMode mode, float range, float repeat)
    {
        var def = new EntityDefinition
        {
            EntityId = id,
            Type = EntityType.StaticObject,
            Transform = new Transform { Position = at, Rotation = facing, Scale = Vector3.One },
        };
        def.SoundEmitter.SoundId = program;
        def.SoundEmitter.Loudspeaker = speaker;
        def.SoundEmitter.Mode = mode;
        def.SoundEmitter.Volume = 1f;
        def.SoundEmitter.Range = range;
        def.SoundEmitter.RepeatIntervalSeconds = repeat;
        h.World.RegisterDefinition(def);
    }

    /// <summary>One start, as the mixer plays it: the world time it was asked for, where in its sound it starts
    /// and after how long, and its flight to the ear.</summary>
    private readonly record struct Start(int Id, double World, double From, double Delay, double Flight, double Length)
    {
        /// <summary>The world time what is heard from its start left the speaker, less where in the sound that
        /// is: the program's top on this voice's clock. One for every voice of a program, mod its length.</summary>
        public double Top => World + Delay - Flight - From;
    }

    /// <summary>Ticks <paramref name="frames"/> frames, moving the listener by <paramref name="walk"/> each, and
    /// reads every start among <paramref name="ids"/> as the mixer would play it. A line ends when the mixer would
    /// have played it out (the recording mixer plays nothing, so it would never end).</summary>
    private static void Run(ClientAudioHarness h, int frames, IReadOnlySet<int> ids, List<Start> starts, Func<Vector3, Vector3>? walk = null)
    {
        for (int f = 0; f < frames; f++)
        {
            if (walk != null) h.StandAt(walk(h.Player.Position));
            foreach (var s in starts.GroupBy(s => s.Id).Select(g => g.Last()))
                if (h.Mixer.Latest.TryGetValue(s.Id, out var playing) && playing.Mode == PlaybackMode.Single
                    && h.WorldTime >= s.World + s.Delay + s.Length - s.From)
                    h.Mixer.Live.Remove(s.Id);
            int seen = h.Mixer.Started.Count;
            h.Tick();
            for (int i = seen; i < h.Mixer.Started.Count; i++)
            {
                var e = h.Mixer.Started[i];
                if (!ids.Contains(e.EntityId)) continue;
                double length = h.Mixer.Registered.GetValueOrDefault(e.SoundId);
                double flight = e.DelayMs / 1000.0;
                var (from, delay) = e.ProgramStart != 0.0
                    ? SharedPrograms.Join(e.ProgramStart, h.WorldTime, flight, length, e.Mode == PlaybackMode.LoopOne)
                    : (0.0, flight);
                starts.Add(new Start(e.EntityId, h.WorldTime, from, delay, flight, length));
            }
        }
    }

    /// <summary>Ticks until the program's render through <paramref name="speaker"/> is in (a worker makes it).</summary>
    private static void Rendered(ClientAudioHarness h, string speaker)
        => Assert.True(h.TickUntil(() => h.Mixer.Registered.Keys.Any(k => k.Contains(speaker, StringComparison.OrdinalIgnoreCase)), 3000),
                       $"the program was never rendered through the {speaker}");

    /// <summary>How far apart a set of program tops are, s, on a loop of <paramref name="length"/> (0: a line).</summary>
    private static double Spread(IEnumerable<double> tops, double length)
    {
        var t = tops.ToList();
        if (length <= 0.0) return t.Max() - t.Min();
        // Each against the first, folded into half a loop either side.
        return t.Select(x => Math.Abs(Math.IEEERemainder(x - t[0], length))).Max();
    }

    /// <summary>
    /// The speedway's PA: four horns on one pole (ids 1122 to 1125, as the map has them) say one 2.48 s line every
    /// 20 s. Each horn's line leaves at the same moment, and each is heard after its own flight to the ear. They
    /// were each their own announcer, first firing staggered by id, (id % 7) times 0.9 s: the four said the line
    /// 0.9 s apart on every repeat.
    /// </summary>
    [Fact]
    public void ThePaHornsOnOnePoleSayOneAnnouncement()
    {
        var h = new ClientAudioHarness(worldTimeAt: WorldAt);
        h.Mixer.Recordings["announce/test_line"] = (Program(2.48), Rate);
        var horns = new (int Id, Vector3 At, Quaternion Facing)[]
        {
            (1122, new Vector3(0f, 6f, 1.2f), Quaternion.Identity),
            (1123, new Vector3(1.2f, 6f, 0f), new Quaternion(0f, 0.707107f, 0f, 0.707107f)),
            (1124, new Vector3(0f, 6f, -1.2f), new Quaternion(0f, 1f, 0f, 0f)),
            (1125, new Vector3(-1.2f, 6f, 0f), new Quaternion(0f, -0.707107f, 0f, 0.707107f)),
        };
        foreach (var (id, at, facing) in horns)
            AddSpeaker(h, id, at, facing, "announce/test_line", "pa_horn", PlaybackMode.Single, 400f, 20f);
        h.StandAt(new Vector3(30f, 0f, 8f));
        var ids = horns.Select(x => x.Id).ToHashSet();
        var starts = new List<Start>();
        Rendered(h, "pa_horn");
        // Three repeats; each line ends long before the next.
        Run(h, (int)(65 * ClientAudioSystem.UpdateHz), ids, starts, walk: null);
        foreach (var s in starts)
            _o.WriteLine($"horn {s.Id}: asked at {s.World - WorldAt:F3} s, its line leaves the horn at {s.Top - WorldAt:F3} s, "
                       + $"heard from {s.Top + s.Flight - WorldAt:F3} s ({s.Flight * 1000:F1} ms of flight)");

        Assert.True(starts.Count >= 4 * 3, $"each horn speaks every 20 s: {starts.Count} lines in 65 s");
        // Rounds: the starts whose lines left within a second of each other.
        var rounds = starts.OrderBy(s => s.Top).Aggregate(new List<List<Start>>(), (acc, s) =>
        {
            if (acc.Count == 0 || s.Top - acc[^1][0].Top > 1.0) acc.Add(new List<Start>());
            acc[^1].Add(s);
            return acc;
        });
        foreach (var round in rounds)
        {
            Assert.Equal(ids.OrderBy(i => i), round.Select(s => s.Id).OrderBy(i => i));
            double spread = Spread(round.Select(s => s.Top), 0.0);
            Assert.True(spread < 1e-3, $"one announcement leaves every horn together: {spread * 1000:F0} ms apart");
            // Heard from its top, on its first sample, after its own flight from where you stand.
            foreach (var s in round)
            {
                Assert.Equal(0.0, s.From, 6);
                var at = horns.First(x => x.Id == s.Id).At;
                double flight = Vector3.Distance(at, h.Player.Position + new Vector3(0f, LocalPlayerStateEye, 0f)) / h.Facade.Atmosphere.SpeedOfSound;
                Assert.True(Math.Abs(s.Flight - flight) < 1e-4, $"horn {s.Id} is heard after its flight, {flight * 1000:F2} ms; {s.Flight * 1000:F2}");
            }
        }
        for (int r = 1; r < rounds.Count; r++)
            Assert.Equal(20.0, rounds[r][0].Top - rounds[r - 1][0].Top, 6);
    }

    private const float LocalPlayerStateEye = OpenFPS.Client.Core.LocalPlayerState.StandingEyeHeight;

    /// <summary>
    /// A horn whose voice goes mid-line (the budget gave it away, the binaural pool was short) comes back into the
    /// line where it is heard, faded in, not from its top: the line is the PA's, not the horn's. Before, it waited
    /// for its next turn; with the line's start keyed to the horn, it would have said it again from the top.
    /// </summary>
    [Fact]
    public void AHornThatLosesItsVoiceMidLineComesBackWhereTheLineIs()
    {
        var h = new ClientAudioHarness(worldTimeAt: WorldAt);
        h.Mixer.Recordings["announce/test_line"] = (Program(2.48), Rate);
        const int Horn = 1124;
        AddSpeaker(h, Horn, new Vector3(0f, 6f, -1.2f), new Quaternion(0f, 1f, 0f, 0f), "announce/test_line", "pa_horn",
                   PlaybackMode.Single, 400f, 20f);
        h.StandAt(new Vector3(30f, 0f, 8f));
        var ids = new HashSet<int> { Horn };
        var starts = new List<Start>();
        Rendered(h, "pa_horn");
        for (int f = 0; f < 21 * ClientAudioSystem.UpdateHz && starts.Count == 0; f++) Run(h, 1, ids, starts);
        Assert.Single(starts);
        var first = starts[0];
        // A second into the line as heard here, the voice is gone.
        Run(h, (int)((first.Top + first.Flight + 1.0 - h.WorldTime) * ClientAudioSystem.UpdateHz), ids, starts);
        h.Mixer.Live.Remove(Horn);
        Run(h, 5, ids, starts);

        Assert.Equal(2, starts.Count);
        var back = starts[1];
        _o.WriteLine($"the line left the horn at {first.Top - WorldAt:F3} s; the voice came back at {back.World - WorldAt:F3} s, "
                   + $"{back.From:F3} s into it, its top on the clock {back.Top - WorldAt:F3} s");
        Assert.Equal(first.Top, back.Top, 6);
        Assert.InRange(back.From, 1.0, 1.2);
        Assert.Equal(0.0, back.Delay, 9);
        Assert.Equal(0.08, SharedPrograms.JoinFade(back.From), 6);
    }

    /// <summary>
    /// One looping program at two speakers 40 m apart, walked between and past, there and back twice: each speaker
    /// goes out of the budget and comes back. Every start, the first and each return, is the program where the world's
    /// clock has it, heard after its own flight; none starts it from its top. Before, the far one started from its
    /// top when it was first heard (17.9 s after the near one: half a second out on a 2.48 s loop), and each came
    /// back from its top again.
    /// </summary>
    [Fact]
    public void SpeakersPlayingOneLoopKeepOneClockAsYouWalkBetweenThem()
    {
        var h = new ClientAudioHarness(worldTimeAt: WorldAt);
        h.Mixer.Recordings["beacons/test_loop"] = (Program(2.48), Rate);
        const int A = 2001, B = 2002;
        var facing = new Quaternion(0f, 0.707107f, 0f, 0.707107f);
        AddSpeaker(h, A, new Vector3(0f, 2f, 0f), facing, "beacons/test_loop", "megaphone", PlaybackMode.LoopOne, 25f, 0f);
        AddSpeaker(h, B, new Vector3(40f, 2f, 0f), Quaternion.Inverse(facing), "beacons/test_loop", "megaphone", PlaybackMode.LoopOne, 25f, 0f);
        var ids = new HashSet<int> { A, B };
        var starts = new List<Start>();
        // One voice to give: the nearer speaker has it, and walking between them hands it over and back (a
        // megaphone's level is heard far past its 25 m, so it is the budget that lets one go, as in a busy street).
        h.Facade.BudgetForLab = 1;
        h.StandAt(new Vector3(-10f, 0f, 3f));
        Rendered(h, "megaphone");

        // A jog (the game's walk) from 10 m west of A to 10 m east of B and back, three times.
        const float Speed = 5f;
        float step = Speed / (float)ClientAudioSystem.UpdateHz;
        Vector3 East(Vector3 p) => p + new Vector3(step, 0f, 0f);
        Vector3 West(Vector3 p) => p - new Vector3(step, 0f, 0f);
        int leg = (int)(60f / step);
        for (int i = 0; i < 3; i++)
        {
            Run(h, leg, ids, starts, East);
            Run(h, leg, ids, starts, West);
        }

        foreach (var s in starts)
            _o.WriteLine($"{(s.Id == A ? "A" : "B")} started at {s.World - WorldAt:F2} s, {s.From:F3} s into its {s.Length:F2} s, "
                       + $"after {s.Delay * 1000:F0} ms; its program's top on the clock {Math.IEEERemainder(s.Top, s.Length):F4} s");
        Assert.True(starts.Count(s => s.Id == A) >= 3 && starts.Count(s => s.Id == B) >= 3,
            $"each speaker comes back more than once: A {starts.Count(s => s.Id == A)}, B {starts.Count(s => s.Id == B)} starts");
        double length = starts[0].Length;
        Assert.True(length > 2.0, $"the loop's render is {length:F2} s");
        double spread = Spread(starts.Select(s => s.Top), length);
        Assert.True(spread < 1e-3, $"every start is the one clock, each heard after its own flight: {spread * 1000:F0} ms apart");
        Assert.Contains(starts, s => s.From > 0.1);
    }

    /// <summary>The /tp spots handed to Cody for the PA (todo.md, waiting on the ear): in the infield, 5 m east of
    /// the pole and 60 m east of it, each one a body stands on (StandingSpots, the server's own check).</summary>
    [Theory]
    [InlineData(5f, 0f, "5.0 0.0 0.2")]
    [InlineData(60f, 0f, "60.0 0.0 0.2")]
    public void TheSpeedwayPaListeningSpotsCanBeStoodOn(float east, float north, string typed)
    {
        var prefabs = new OpenFPS.Server.Repositories.PrefabRepository(Path.Combine(AppContext.BaseDirectory, "prefabs"));
        var maps = new OpenFPS.Server.Repositories.MapRepository(Path.Combine(AppContext.BaseDirectory, "maps"));
        var manager = new OpenFPS.Server.Core.MapManager(maps, prefabs);
        manager.Initialize();
        Assert.True(manager.TryGetMap("speedway", out Arch.Core.World world, out _, out var grid, out _));
        var spot = OpenFPS.Server.Core.StandingSpots.In(world, grid, OpenFPS.Common.PlayerCoordinates.ToWorld(east, north, 1.5f),
                                                         Quaternion.Identity, new Vector3(3f, 4f, 3f));
        string found = spot is { } s ? OpenFPS.Server.Core.StandingSpots.Typed(s) : "none";
        _o.WriteLine($"{east} m east, {north} m north: /tp {found}");
        Assert.Equal(typed, found);
    }

    /// <summary>The clock's arithmetic: a line waits out its flight from its top, joins mid-line where it is heard,
    /// is not started once over; a loop joins anywhere; the next firing is handed over just ahead of leaving.</summary>
    [Fact]
    public void TheClockJoinsWhereTheProgramIsHeard()
    {
        // A line that left 0.05 s ago, 0.2 s of flight away: its top is still in the air.
        var (from, delay) = SharedPrograms.Join(100.0, 100.05, 0.2, 2.48, loop: false);
        Assert.Equal(0.0, from, 9);
        Assert.Equal(0.15, delay, 9);
        // 1.2 s on: heard from a second in.
        (from, delay) = SharedPrograms.Join(100.0, 101.2, 0.2, 2.48, loop: false);
        Assert.Equal(1.0, from, 9);
        Assert.Equal(0.0, delay, 9);
        // Over here.
        Assert.True(double.IsNaN(SharedPrograms.Join(100.0, 103.0, 0.2, 2.48, loop: false).From));
        // A loop, any time: where the clock has it, less the flight.
        (from, delay) = SharedPrograms.Join(100.0, 110.0, 0.2, 2.48, loop: true);
        Assert.Equal(9.8 % 2.48, from, 9);
        Assert.Equal(0.0, delay, 9);

        const string Key = "announce/x|20";
        double phase = SharedPrograms.Phase(Key, 20.0);
        Assert.InRange(phase, 1e-9, 20.0 - 1e-9);
        double top = WorldAt - (WorldAt - phase) % 20.0;
        // Just before a top leaves: that one, handed over early.
        Assert.Equal(top + 20.0, SharedPrograms.Firing(Key, 20.0, top + 19.95, 0.1, 2.48), 6);
        // Mid-line here: the one being heard.
        Assert.Equal(top, SharedPrograms.Firing(Key, 20.0, top + 1.0, 0.1, 2.48), 6);
        // Its last quarter second, and after: none.
        Assert.True(double.IsNaN(SharedPrograms.Firing(Key, 20.0, top + 2.4, 0.1, 2.48)));
        Assert.True(double.IsNaN(SharedPrograms.Firing(Key, 20.0, top + 10.0, 0.1, 2.48)));
        // A join mid-word fades in over the budget's 80 ms; from the top, none.
        Assert.Equal(0.0, SharedPrograms.JoinFade(0.0));
        Assert.Equal(0.08, SharedPrograms.JoinFade(1.0), 6);
        Assert.Equal(0.006, SharedPrograms.JoinFade(0.001), 6);
    }
}

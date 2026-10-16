using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using OpenFPS.Common;
using OpenFPS.Instruments;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The stage 0 physics replays run alone: MovementSystem keeps its gather's scratch lists in statics for the
/// one tick thread, so two classes stepping it at once would share them and part a walk from its track.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PhysicsReplayCollection
{
    public const string Name = "Physics replays";
}

/// <summary>
/// Where the replays' stored copies live (OpenFPS.Tests/PhysicsBoundary) and the rule for changing them:
/// only in the commit that changes the physics on purpose, by running the test with OPENFPS_PHYSICS_WRITE=1,
/// and that commit says why (docs/PHYSICS_BOUNDARY.md, Stage 0 as built).
///
/// Each store's first line is the maths it was made on (<see cref="CrossMachine.MathsFingerprint"/>). On the
/// same maths a run is compared to float rounding; on other maths by <see cref="CrossMachine"/>'s drift and
/// slip (14.9). OPENFPS_PHYSICS_FOREIGN=1 compares as other maths would, to see that rule here.
/// </summary>
public static class PhysicsStore
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    public static string PathOf(params string[] parts) => Path.Combine([RepoRoot(), "OpenFPS.Tests", "PhysicsBoundary", .. parts]);

    public static bool Writing => Environment.GetEnvironmentVariable("OPENFPS_PHYSICS_WRITE") == "1";

    public static PhysicsReplays.MapFolders Folders => new(Path.Combine(AppContext.BaseDirectory, "maps"),
        Path.Combine(AppContext.BaseDirectory, "places"), Path.Combine(AppContext.BaseDirectory, "prefabs"));

    private const string MathsLine = "# maths ";

    /// <summary>This machine's physics maths, worked out once.</summary>
    public static string Maths => MathsOnce.Value;
    private static readonly Lazy<string> MathsOnce = new(CrossMachine.MathsFingerprint);

    public static bool Foreign => Environment.GetEnvironmentVariable("OPENFPS_PHYSICS_FOREIGN") == "1";

    /// <summary>A store's maths line and the rest; no maths ("") for a store written before it had one.</summary>
    public static (string Maths, string Text) ReadStored(string path)
    {
        string all = Read(path).Replace("\r\n", "\n");
        if (!all.StartsWith(MathsLine, StringComparison.Ordinal)) return ("", all);
        int end = all.IndexOf('\n');
        return (all[MathsLine.Length..end], all[(end + 1)..]);
    }

    /// <summary>Writes a store, this machine's maths first.</summary>
    public static void Store(string path, string text) => Write(path, MathsLine + Maths + "\n" + text);

    /// <summary>Whether a store's maths are this machine's (and not overridden by OPENFPS_PHYSICS_FOREIGN).</summary>
    public static bool SameMaths(string storedMaths) => storedMaths == Maths && !Foreign;

    public static string Read(string path)
    {
        if (!File.Exists(path)) return "";
        if (!path.EndsWith(".gz", StringComparison.Ordinal)) return File.ReadAllText(path);
        using var gz = new GZipStream(File.OpenRead(path), CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!path.EndsWith(".gz", StringComparison.Ordinal)) { File.WriteAllText(path, text); return; }
        using var file = File.Create(path);
        using var gz = new GZipStream(file, CompressionLevel.SmallestSize);
        var bytes = Encoding.UTF8.GetBytes(text);
        gz.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Compares a run with its stored copy, or stores it when writing: to <paramref name="tolerance"/>
    /// on the maths it was stored on, by <paramref name="other"/> on any other. Fails on the first line past its
    /// bound, both lines shown.</summary>
    public static void Check(string path, string now, Func<int, double> tolerance, CrossMachine other, string what,
                             Func<int, bool>? angle = null, Action<string>? log = null)
    {
        if (Writing) { Store(path, now); return; }
        var (maths, stored) = ReadStored(path);
        Assert.False(stored.Length == 0, $"no stored {what} at {path}: run with OPENFPS_PHYSICS_WRITE=1 to make it");
        string? diff;
        string how;
        if (SameMaths(maths))
        {
            diff = PhysicsReplays.Compare(stored, now, tolerance, angle);
            how = "the maths it was stored on, compared to float rounding";
        }
        else
        {
            diff = other.Compare(stored, now, out int slipped);
            string whose = Foreign ? "OPENFPS_PHYSICS_FOREIGN" : maths.Length == 0 ? "none stored" : maths + " stored";
            how = $"other maths ({whose}; {Maths} here), compared within the drift, {slipped} rows as slips";
        }
        log?.Invoke($"{what}: {how}");
        Assert.True(diff == null, $"{what} changed from its stored copy ({Path.GetFileName(path)}) on {how}. If the change is intended, "
            + $"regenerate with OPENFPS_PHYSICS_WRITE=1 and say why in the commit.\n{diff}");
    }

    /// <summary>Maps loaded once per class, by id.</summary>
    public sealed class Maps
    {
        private readonly ConcurrentDictionary<string, Lazy<PhysicsReplays.LoadedMap>> _maps = new();
        public PhysicsReplays.LoadedMap Get(string id)
            => _maps.GetOrAdd(id, k => new Lazy<PhysicsReplays.LoadedMap>(() => PhysicsReplays.Load(k, Folders))).Value;
    }
}

/// <summary>
/// The movement replay of physics stage 0: every stored walk through the server's MovementSystem.Update and
/// the client's ClientPhysicsSystem.Predict, the server's track compared with walks/NAME.tsv, the gap between
/// the two sides held to 1 mm (or to a walk's known gap, which only goes down), and the three parity risks
/// of the survey measured into walks.tsv.
/// </summary>
[Collection(PhysicsReplayCollection.Name)]
public class MovementReplayTests : IClassFixture<PhysicsStore.Maps>
{
    private readonly PhysicsStore.Maps _maps;
    private readonly ITestOutputHelper _o;
    private static readonly object SummaryLock = new();

    public MovementReplayTests(PhysicsStore.Maps maps, ITestOutputHelper o) { _maps = maps; _o = o; }

    public static IEnumerable<object[]> WalkNames() => PhysicsReplays.Walks.Select(w => new object[] { w.Name });

    /// <summary>Positions to 0.1 mm, velocities to 1 mm/s, the gap to 0.05 mm: past float rounding on
    /// another machine's maths library, well short of a change anybody would make.</summary>
    private static double TrackTolerance(int column) => column switch { 2 or 3 or 4 => 1e-4, 5 or 6 or 7 => 1e-3, 9 => 0.05, _ => 0 };

    /// <summary>On other maths: the same, growing 1 mm and 1 mm/s a second; a body a tick or two along its way
    /// (by its velocity) passes as a slip, its velocity, grounded flag and gap not compared on that row.</summary>
    internal static readonly CrossMachine OtherMaths = new()
    {
        Seconds = r => double.Parse(r[0], CultureInfo.InvariantCulture) / PhysicsConstants.TickRate,
        Plane = (2, 4),
        Rule = c => c switch
        {
            2 or 4 => new(CrossMachine.Kind.Horizontal, 1e-4, CrossMachine.MetresPerSecond),
            3 => new(CrossMachine.Kind.Height, 1e-4, CrossMachine.MetresPerSecond),
            5 or 6 or 7 => new(CrossMachine.Kind.Follows, 1e-3, CrossMachine.SpeedPerSecond),
            8 => new(CrossMachine.Kind.Follows, 0),
            9 => new(CrossMachine.Kind.Follows, 0.05),
            _ => null,
        },
        Speed = r => new Vector2(Num(r[5]), Num(r[7])).Length(),
        Climb = r => Num(r[6]),
        HeadingOf = r => Num(r[5]) * Num(r[5]) + Num(r[7]) * Num(r[7]) > 0.01f ? Math.Atan2(Num(r[5]), Num(r[7])) : null,
    };

    private static float Num(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    [Theory]
    [MemberData(nameof(WalkNames))]
    public void TheWalkTakesItsStoredTrack(string name)
    {
        var script = PhysicsReplays.Walks.Single(w => w.Name == name);
        var map = _maps.Get(script.Map);
        var run = PhysicsReplays.Walk(map, script);
        var again = PhysicsReplays.Walk(map, script);
        string track = PhysicsReplays.Track(run);
        Assert.True(track == PhysicsReplays.Track(again), "the same walk twice took two tracks: something in the step is not deterministic");
        _o.WriteLine(PhysicsReplays.WalkSummaryHeader);
        _o.WriteLine(PhysicsReplays.SummaryLine(run));

        Assert.True(run.ReproductionMisses == 0,
            $"{run.ReproductionMisses} ticks where the rig's copy of MovementSystem's step disagreed with it: the risks it measures are of another step");
        Assert.True(run.ClockStalls == 0, "the machine stalled past the ground memo's 150 ms in a tick, three times running");

        PhysicsStore.Check(PhysicsStore.PathOf("walks", name + ".tsv"), track, TrackTolerance, OtherMaths, $"walk {name}", log: _o.WriteLine);
        CheckSummary(run);

        float gapMm = run.WorstGapMetres * 1000f;
        if (script.KnownGapMm > 0f)
        {
            Assert.True(gapMm <= script.KnownGapMm, $"{name}: the server and the client part by {gapMm:F1} mm, past the {script.KnownGapMm} mm known ({script.KnownWhy})");
            Assert.True(gapMm > 1f, $"{name}: the known gap is gone ({gapMm:F2} mm): good. Take KnownGapMm off the walk.");
        }
        else Assert.True(gapMm <= 1f, $"{name}: the server and the client part by {gapMm:F2} mm on {run.GapTicks} ticks (1 mm allowed)");
    }

    /// <summary>The walk's row of walks.tsv, the risks with it; rewritten in place when writing.</summary>
    private static void CheckSummary(PhysicsReplays.WalkRun run)
    {
        string path = PhysicsStore.PathOf("walks.tsv");
        string line = PhysicsReplays.SummaryLine(run);
        lock (SummaryLock)
        {
            var (maths, text) = PhysicsStore.ReadStored(path);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
            int at = lines.FindIndex(l => l.Split('\t')[0] == run.Script.Name);
            if (PhysicsStore.Writing)
            {
                if (lines.Count == 0) lines.Add(PhysicsReplays.WalkSummaryHeader);
                if (at >= 0) lines[at] = line; else lines.Add(line);
                var order = PhysicsReplays.Walks.Select(w => w.Name).ToList();
                var body = lines.Skip(1).OrderBy(l => order.IndexOf(l.Split('\t')[0])).ToList();
                PhysicsStore.Store(path, PhysicsReplays.WalkSummaryHeader + "\n" + string.Join("\n", body) + "\n");
                return;
            }
            Assert.True(at >= 0, $"walks.tsv has no row for {run.Script.Name}: run with OPENFPS_PHYSICS_WRITE=1");
            // On other maths a slip can move a count by the tick or two it moved, and a worst case by half a millimetre.
            Func<int, double> tolerance = PhysicsStore.SameMaths(maths) ? c => c == 3 ? 0.01 : 0.15
                : c => c switch { 3 => 0.01, 4 or 8 or 11 or 12 or 15 => 0.5, 2 => 0, _ => 2 };
            string? diff = PhysicsReplays.Compare(lines[at], line, tolerance);
            Assert.True(diff == null, $"{run.Script.Name}'s risks changed from walks.tsv:\n{diff}");
        }
    }
}

/// <summary>
/// The driving, traffic and train replays of physics stage 0: a driven car (throttle, brake, reverse, a
/// turn, a wall, a hill) through DrivingSystem.Update, the city's traffic for a minute through VehicleSystem
/// and its trains for a minute and a half through RailSystem, each compared with its stored track.
/// </summary>
[Collection(PhysicsReplayCollection.Name)]
public class DrivingReplayTests : IClassFixture<PhysicsStore.Maps>
{
    private readonly PhysicsStore.Maps _maps;
    private readonly ITestOutputHelper _o;
    public DrivingReplayTests(PhysicsStore.Maps maps, ITestOutputHelper o) { _maps = maps; _o = o; }

    public static IEnumerable<object[]> DriveNames() => PhysicsReplays.Drives.Select(d => new object[] { d.Name });

    /// <summary>Positions to 0.5 mm, angles and speeds to 1e-3, the quaternion to 1e-4; the wheels' bytes to
    /// one step of their encoding.</summary>
    private static double DriveTolerance(int column) => column switch
    {
        2 or 3 or 4 => 5e-4, 5 or 6 or 7 or 8 or 9 or 14 => 1e-3, 10 or 11 or 12 or 13 => 1e-4, >= 15 => 1, _ => 0,
    };

    /// <summary>On other maths: the same, growing 1 mm, 1 mm/s and 1e-4 rad a second and a wheel step a minute;
    /// a car a tick or two along its way passes as a slip, its controls, pose and wheels not compared on that row.</summary>
    internal static readonly CrossMachine DriveOtherMaths = new()
    {
        Seconds = r => double.Parse(r[0], CultureInfo.InvariantCulture) / PhysicsConstants.TickRate,
        Plane = (2, 4),
        Rule = c => c switch
        {
            2 or 4 => new(CrossMachine.Kind.Horizontal, 5e-4, CrossMachine.MetresPerSecond),
            3 => new(CrossMachine.Kind.Height, 5e-4, CrossMachine.MetresPerSecond),
            5 => new(CrossMachine.Kind.Heading, 1e-3, CrossMachine.RadiansPerSecond),
            6 => new(CrossMachine.Kind.Speed, 1e-3, CrossMachine.SpeedPerSecond),
            7 or 8 or 9 or 14 => new(CrossMachine.Kind.Follows, 1e-3, CrossMachine.SpeedPerSecond),
            10 or 11 or 12 or 13 => new(CrossMachine.Kind.Follows, 1e-4, CrossMachine.RadiansPerSecond),
            >= 15 => new(CrossMachine.Kind.Follows, 1, 1.0 / 60),
            _ => null,
        },
        Speed = r => double.Parse(r[6], CultureInfo.InvariantCulture),
        HeadingOf = r => double.Parse(r[5], CultureInfo.InvariantCulture),
    };

    /// <summary>The traffic's and the trains' rule on other maths: positions 2 mm growing 1 mm a second, the
    /// yaw and speed likewise, the wheels a step a minute; a mover a tick or two along its way is a slip.</summary>
    internal static readonly CrossMachine MoverOtherMaths = new()
    {
        Seconds = r => double.Parse(r[0], CultureInfo.InvariantCulture),
        Plane = (3, 5),
        Rule = c => c switch
        {
            3 or 5 => new(CrossMachine.Kind.Horizontal, 2e-3, CrossMachine.MetresPerSecond),
            4 => new(CrossMachine.Kind.Height, 2e-3, CrossMachine.MetresPerSecond),
            6 => new(CrossMachine.Kind.Heading, 2e-3, CrossMachine.RadiansPerSecond),
            7 => new(CrossMachine.Kind.Speed, 2e-3, CrossMachine.SpeedPerSecond),
            >= 8 => new(CrossMachine.Kind.Follows, 1, 1.0 / 60),
            _ => null,
        },
        Speed = r => double.Parse(r[7], CultureInfo.InvariantCulture),
        HeadingOf = r => double.Parse(r[6], CultureInfo.InvariantCulture),
    };

    [Theory]
    [MemberData(nameof(DriveNames))]
    public void TheDriveTakesItsStoredTrack(string name)
    {
        var script = PhysicsReplays.Drives.Single(d => d.Name == name);
        var map = _maps.Get(script.Map);
        string track = PhysicsReplays.Drive(map, script);
        Assert.True(track == PhysicsReplays.Drive(map, script), "the same drive twice took two tracks");
        PhysicsStore.Check(PhysicsStore.PathOf("drives", name + ".tsv"), track, DriveTolerance, DriveOtherMaths, $"drive {name}", c => c == 5, _o.WriteLine);
    }

    private static double MoverTolerance(int column) => column switch { 3 or 4 or 5 or 6 or 7 => 2e-3, >= 8 => 1, _ => 0 };

    [Fact]
    public void TheCitysTrafficTakesItsStoredTracks()
    {
        string text = PhysicsReplays.Traffic(PhysicsReplays.Load("city", PhysicsStore.Folders));
        Assert.True(text == PhysicsReplays.Traffic(PhysicsReplays.Load("city", PhysicsStore.Folders)), "the same minute of traffic twice ran two ways");
        PhysicsStore.Check(PhysicsStore.PathOf("traffic.tsv.gz"), text, MoverTolerance, MoverOtherMaths, "the city's traffic", c => c == 6, _o.WriteLine);
    }
}

/// <summary>The city's trains through RailSystem for a minute and a half, compared with rail.tsv.gz.</summary>
[Collection(PhysicsReplayCollection.Name)]
public class RailReplayTests
{
    private readonly ITestOutputHelper _o;
    public RailReplayTests(ITestOutputHelper o) => _o = o;

    [Fact]
    public void TheCitysTrainsTakeTheirStoredTracks()
    {
        string text = PhysicsReplays.Rail(PhysicsReplays.Load("city", PhysicsStore.Folders));
        Assert.True(text == PhysicsReplays.Rail(PhysicsReplays.Load("city", PhysicsStore.Folders)), "the same trains twice ran two ways");
        PhysicsStore.Check(PhysicsStore.PathOf("rail.tsv.gz"), text, c => c is 3 or 4 or 5 or 6 or 7 ? 2e-3 : c >= 8 ? 1 : 0,
            DrivingReplayTests.MoverOtherMaths, "the city's trains", c => c == 6, _o.WriteLine);
    }
}

/// <summary>
/// The ballistics table: ExternalBallistics.Fly for every weapon zeroed at 100 m, level, raised 5 and 30
/// degrees, in a crosswind and on a hot day at altitude, at 50 m or 100 m steps, compared with
/// ballistics.tsv to a tenth of a millimetre of height and drift, a microsecond and a centimetre a second.
/// </summary>
public class BallisticsTableTests
{
    private readonly ITestOutputHelper _o;
    public BallisticsTableTests(ITestOutputHelper o) => _o = o;

    /// <summary>On other maths: the same, growing with the time of flight (1 mm of height and drift, 10 us and
    /// 1 cm/s a second). A flight has no decisions to slip, so nothing passes as one.</summary>
    private static readonly CrossMachine OtherMaths = new()
    {
        Seconds = r => double.Parse(r[6], CultureInfo.InvariantCulture),
        Rule = c => c switch
        {
            2 => new(CrossMachine.Kind.Drift, 2e-4), 3 => new(CrossMachine.Kind.Drift, 0.05),
            4 or 5 => new(CrossMachine.Kind.Drift, 2e-4, CrossMachine.MetresPerSecond),
            6 => new(CrossMachine.Kind.Drift, 2e-5, 1e-5), 7 => new(CrossMachine.Kind.Drift, 0.02, 0.01),
            _ => null,
        },
    };

    [Fact]
    public void EveryWeaponFliesItsStoredTable()
    {
        PhysicsStore.Check(PhysicsStore.PathOf("ballistics.tsv"), PhysicsReplays.BallisticsTable(),
            c => c switch { 2 => 2e-4, 3 => 0.05, 4 or 5 => 2e-4, 6 => 2e-5, 7 => 0.02, _ => 0 }, OtherMaths, "the ballistics table", log: _o.WriteLine);
    }
}

/// <summary>
/// The ricochet table: every weapon's round onto every face that skips one, at three shares of its critical
/// angle with three dice, how it leaves and what it is afterwards, and its tumbling flight half a second on,
/// compared with ricochet.tsv. Stored before the bounce and the flight moved to OpenFPS.Physics (physics stage
/// 2, docs/PHYSICS_BOUNDARY.md 16.5), so on the same maths nothing may differ in any digit.
/// </summary>
public class RicochetTableTests
{
    private readonly ITestOutputHelper _o;
    public RicochetTableTests(ITestOutputHelper o) => _o = o;

    /// <summary>On other maths: a few units in the last place stored of the bounce, a tenth of a millimetre of
    /// the flight's half second. Whether it skips is compared as text: the dice draw it well clear of a tie.</summary>
    private static readonly CrossMachine OtherMaths = new()
    {
        Seconds = _ => PhysicsReplays.RicochetFlightSeconds,
        Rule = c => c switch
        {
            5 or 6 or 7 => new(CrossMachine.Kind.Drift, 5e-4), 8 => new(CrossMachine.Kind.Drift, 5e-3),
            9 or 10 or 11 => new(CrossMachine.Kind.Drift, 5e-5), 12 => new(CrossMachine.Kind.Drift, 0.05),
            13 => new(CrossMachine.Kind.Drift, 5e-3), 14 => new(CrossMachine.Kind.Drift, 5e-4), 15 => new(CrossMachine.Kind.Drift, 5e-6),
            16 or 17 or 18 or 20 => new(CrossMachine.Kind.Drift, 1e-4, CrossMachine.MetresPerSecond),
            19 => new(CrossMachine.Kind.Drift, 5e-4, CrossMachine.SpeedPerSecond),
            _ => null,
        },
    };

    [Fact]
    public void EveryRoundBouncesAndFliesItsStoredTable()
    {
        string table = PhysicsReplays.RicochetTable();
        Assert.True(table == PhysicsReplays.RicochetTable(), "the same bounces twice ran two ways");
        var rows = table.TrimEnd('\n').Split('\n').Skip(1).ToList();
        int bounced = rows.Count(r => r.Split('\t')[4] == "1");
        _o.WriteLine($"{rows.Count} strikes, {bounced} ricochets");
        Assert.True(bounced > rows.Count / 3 && bounced < rows.Count, $"{bounced} of {rows.Count} skipped: the table should hold both outcomes");
        PhysicsStore.Check(PhysicsStore.PathOf("ricochet.tsv"), table, _ => 0, OtherMaths, "the ricochet table", log: _o.WriteLine);
    }
}

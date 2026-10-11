using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
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
/// </summary>
public static class PhysicsStore
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    public static string PathOf(params string[] parts) => Path.Combine([RepoRoot(), "OpenFPS.Tests", "PhysicsBoundary", .. parts]);

    public static bool Writing => Environment.GetEnvironmentVariable("OPENFPS_PHYSICS_WRITE") == "1";

    public static PhysicsReplays.MapFolders Folders => new(Path.Combine(AppContext.BaseDirectory, "maps"),
        Path.Combine(AppContext.BaseDirectory, "places"), Path.Combine(AppContext.BaseDirectory, "prefabs"));

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

    /// <summary>Compares a run with its stored copy, or stores it when writing. Fails on the first line that
    /// moved past its column's tolerance, both lines shown.</summary>
    public static void Check(string path, string now, Func<int, double> tolerance, string what, Func<int, bool>? angle = null)
    {
        if (Writing) { Write(path, now); return; }
        string stored = Read(path);
        Assert.False(stored.Length == 0, $"no stored {what} at {path}: run with OPENFPS_PHYSICS_WRITE=1 to make it");
        string? diff = PhysicsReplays.Compare(stored, now, tolerance, angle);
        Assert.True(diff == null, $"{what} changed from its stored copy ({Path.GetFileName(path)}). If the change is intended, "
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

        PhysicsStore.Check(PhysicsStore.PathOf("walks", name + ".tsv"), track, TrackTolerance, $"walk {name}");
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
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            int at = lines.FindIndex(l => l.Split('\t')[0] == run.Script.Name);
            if (PhysicsStore.Writing)
            {
                if (lines.Count == 0) lines.Add(PhysicsReplays.WalkSummaryHeader);
                if (at >= 0) lines[at] = line; else lines.Add(line);
                var order = PhysicsReplays.Walks.Select(w => w.Name).ToList();
                var body = lines.Skip(1).OrderBy(l => order.IndexOf(l.Split('\t')[0])).ToList();
                File.WriteAllText(path, PhysicsReplays.WalkSummaryHeader + "\n" + string.Join("\n", body) + "\n");
                return;
            }
            Assert.True(at >= 0, $"walks.tsv has no row for {run.Script.Name}: run with OPENFPS_PHYSICS_WRITE=1");
            string? diff = PhysicsReplays.Compare(lines[at], line, c => c == 3 ? 0.01 : 0.15);
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
    public DrivingReplayTests(PhysicsStore.Maps maps) => _maps = maps;

    public static IEnumerable<object[]> DriveNames() => PhysicsReplays.Drives.Select(d => new object[] { d.Name });

    /// <summary>Positions to 0.5 mm, angles and speeds to 1e-3, the quaternion to 1e-4; the wheels' bytes to
    /// one step of their encoding.</summary>
    private static double DriveTolerance(int column) => column switch
    {
        2 or 3 or 4 => 5e-4, 5 or 6 or 7 or 8 or 9 or 14 => 1e-3, 10 or 11 or 12 or 13 => 1e-4, >= 15 => 1, _ => 0,
    };

    [Theory]
    [MemberData(nameof(DriveNames))]
    public void TheDriveTakesItsStoredTrack(string name)
    {
        var script = PhysicsReplays.Drives.Single(d => d.Name == name);
        var map = _maps.Get(script.Map);
        string track = PhysicsReplays.Drive(map, script);
        Assert.True(track == PhysicsReplays.Drive(map, script), "the same drive twice took two tracks");
        PhysicsStore.Check(PhysicsStore.PathOf("drives", name + ".tsv"), track, DriveTolerance, $"drive {name}", c => c == 5);
    }

    private static double MoverTolerance(int column) => column switch { 3 or 4 or 5 or 6 or 7 => 2e-3, >= 8 => 1, _ => 0 };

    [Fact]
    public void TheCitysTrafficTakesItsStoredTracks()
    {
        string text = PhysicsReplays.Traffic(PhysicsReplays.Load("city", PhysicsStore.Folders));
        Assert.True(text == PhysicsReplays.Traffic(PhysicsReplays.Load("city", PhysicsStore.Folders)), "the same minute of traffic twice ran two ways");
        PhysicsStore.Check(PhysicsStore.PathOf("traffic.tsv.gz"), text, MoverTolerance, "the city's traffic", c => c == 6);
    }
}

/// <summary>The city's trains through RailSystem for a minute and a half, compared with rail.tsv.gz.</summary>
[Collection(PhysicsReplayCollection.Name)]
public class RailReplayTests
{
    [Fact]
    public void TheCitysTrainsTakeTheirStoredTracks()
    {
        string text = PhysicsReplays.Rail(PhysicsReplays.Load("city", PhysicsStore.Folders));
        Assert.True(text == PhysicsReplays.Rail(PhysicsReplays.Load("city", PhysicsStore.Folders)), "the same trains twice ran two ways");
        PhysicsStore.Check(PhysicsStore.PathOf("rail.tsv.gz"), text, c => c is 3 or 4 or 5 or 6 or 7 ? 2e-3 : c >= 8 ? 1 : 0, "the city's trains", c => c == 6);
    }
}

/// <summary>
/// The ballistics table: ExternalBallistics.Fly for every weapon zeroed at 100 m, level, raised 5 and 30
/// degrees, in a crosswind and on a hot day at altitude, at 50 m or 100 m steps, compared with
/// ballistics.tsv to a tenth of a millimetre of height and drift, a microsecond and a centimetre a second.
/// </summary>
public class BallisticsTableTests
{
    [Fact]
    public void EveryWeaponFliesItsStoredTable()
    {
        PhysicsStore.Check(PhysicsStore.PathOf("ballistics.tsv"), PhysicsReplays.BallisticsTable(),
            c => c switch { 2 => 2e-4, 3 => 0.05, 4 or 5 => 2e-4, 6 => 2e-5, 7 => 0.02, _ => 0 }, "the ballistics table");
    }
}

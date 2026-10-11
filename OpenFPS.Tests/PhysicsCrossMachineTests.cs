using System.Globalization;
using OpenFPS.Common;
using OpenFPS.Instruments;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The physics replays' rule for other maths (<see cref="CrossMachine"/>, docs/PHYSICS_BOUNDARY.md 14.9) held
/// to both of its jobs: a run whose every step was rounded differently passes it, and a behaviour changed in
/// the stored traffic does not (a car in the next lane, half a lane over, stopped, or with only its wheels
/// changed), while a car a tick along its way does.
/// </summary>
[Collection(PhysicsReplayCollection.Name)]
public class PhysicsCrossMachineTests
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly ITestOutputHelper _o;
    public PhysicsCrossMachineTests(ITestOutputHelper o) => _o = o;

    private static string StoredTraffic() => PhysicsStore.ReadStored(PhysicsStore.PathOf("traffic.tsv.gz")).Text;

    /// <summary>Every tick's step a float unit longer, shorter or the same, at random: rounding everywhere.</summary>
    private static float RoundedDifferently(int tick)
    {
        uint h = (uint)tick * 2654435761u ^ 81006u;
        h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
        float dt = PhysicsConstants.FixedDeltaTime;
        return (h % 3) switch { 0 => MathF.BitIncrement(dt), 1 => MathF.BitDecrement(dt), _ => dt };
    }

    [Fact]
    public void ATrafficRoundedDifferentlyEveryStepIsWithinTheRule()
    {
        string run = PhysicsReplays.Traffic(PhysicsReplays.Load("city", PhysicsStore.Folders), 60, RoundedDifferently);
        string? diff = DrivingReplayTests.MoverOtherMaths.Compare(StoredTraffic(), run, out int slipped);
        _o.WriteLine($"rounded differently: {slipped} rows as slips");
        Assert.True(diff == null, $"a minute of traffic rounded differently every step is outside the rule for other maths:\n{diff}");
    }

    /// <summary>The fastest wheeled mover at second 30 that runs to the end, and its rows by line.</summary>
    private static (string[] Lines, int Id, Dictionary<int, int> RowOf) Car()
    {
        var lines = StoredTraffic().TrimEnd('\n').Split('\n');
        var rows = lines.Skip(1).Select(l => l.Split('\t')).ToList();
        int id = rows.Where(c => c[0] == "30" && c[8] != "-" && double.Parse(c[7], Inv) > 8)
            .Select(c => int.Parse(c[1], Inv)).Where(i => rows.Any(c => c[0] == "60" && c[1] == i.ToString(Inv))).Min();
        var rowOf = new Dictionary<int, int>();
        for (int i = 1; i < lines.Length; i++)
        {
            var c = lines[i].Split('\t');
            if (c[1] == id.ToString(Inv)) rowOf[int.Parse(c[0], Inv)] = i;
        }
        return (lines, id, rowOf);
    }

    private static string Edit(string line, Action<string[]> change)
    {
        var c = line.Split('\t');
        change(c);
        return string.Join('\t', c);
    }

    private static double D(string s) => double.Parse(s, Inv);
    private static string F3(double v) => v.ToString("F3", Inv);

    private static string? Judge(string[] lines, out int slipped)
        => DrivingReplayTests.MoverOtherMaths.Compare(StoredTraffic(), string.Join('\n', lines) + "\n", out slipped);

    /// <summary>Moves the car from second 30 on by <paramref name="right"/> metres to its right.</summary>
    private static string[] Across(double right)
    {
        var (lines, _, rowOf) = Car();
        lines = (string[])lines.Clone();
        foreach (var (second, i) in rowOf.Where(r => r.Key >= 30))
            lines[i] = Edit(lines[i], c =>
            {
                double yaw = D(c[6]);
                c[3] = F3(D(c[3]) + right * Math.Cos(yaw));
                c[5] = F3(D(c[5]) - right * Math.Sin(yaw));
            });
        return lines;
    }

    [Fact]
    public void DriftWithinTheBoundPasses()
    {
        var lines = StoredTraffic().TrimEnd('\n').Split('\n');
        for (int i = 1; i < lines.Length; i++)
            lines[i] = Edit(lines[i], c =>
            {
                double t = D(c[0]);
                c[3] = F3(D(c[3]) + 0.5e-3 * t);
                c[7] = F3(D(c[7]) - 0.5e-3 * t);
            });
        Assert.Null(Judge(lines, out int slipped));
        Assert.Equal(0, slipped);
        // The rule for the same maths does not allow it: 30 mm at the end of the minute.
        Assert.NotNull(PhysicsReplays.Compare(StoredTraffic(), string.Join('\n', lines) + "\n", c => c is >= 3 and <= 7 ? 2e-3 : c >= 8 ? 1 : 0));
    }

    [Fact]
    public void ACarInTheNextLaneFails()
    {
        string? diff = Judge(Across(3.5), out _);
        _o.WriteLine(diff);
        Assert.NotNull(diff);
        Assert.Contains("across its path", diff);
    }

    [Fact]
    public void ACarHalfALaneOverFails()
    {
        string? diff = Judge(Across(1.0), out _);
        _o.WriteLine(diff);
        Assert.NotNull(diff);
        Assert.Contains("across its path", diff);
    }

    [Fact]
    public void ACarThatStopsFails()
    {
        var (lines, _, rowOf) = Car();
        var held = lines[rowOf[30]].Split('\t');
        foreach (var (second, i) in rowOf.Where(r => r.Key > 30))
            lines[i] = Edit(lines[i], c => { c[3] = held[3]; c[4] = held[4]; c[5] = held[5]; c[6] = held[6]; c[7] = "0.000"; });
        string? diff = Judge(lines, out _);
        _o.WriteLine(diff);
        Assert.NotNull(diff);
    }

    [Fact]
    public void ACarATickAlongItsWayPassesAsASlip()
    {
        var (lines, _, rowOf) = Car();
        int i = rowOf[30];
        lines[i] = Edit(lines[i], c =>
        {
            double yaw = D(c[6]), ahead = D(c[7]) / PhysicsConstants.TickRate;
            c[3] = F3(D(c[3]) + ahead * Math.Sin(yaw));
            c[5] = F3(D(c[5]) + ahead * Math.Cos(yaw));
            c[7] = F3(D(c[7]) - 0.25);
            c[10] = "-60";     // the first wheel's slip ratio: a skid begun a tick early
        });
        Assert.Null(Judge(lines, out int slipped));
        Assert.Equal(1, slipped);
    }

    [Fact]
    public void WheelsChangedWhereTheCarIsTheSameFail()
    {
        var (lines, _, rowOf) = Car();
        int i = rowOf[30];
        lines[i] = Edit(lines[i], c => c[8] = (int.Parse(c[8], Inv) + 5).ToString(Inv));
        string? diff = Judge(lines, out _);
        _o.WriteLine(diff);
        Assert.NotNull(diff);
        Assert.Contains("where it was stored", diff);
    }

    [Fact]
    public void AWalkATreadLowerFails()
    {
        var (_, stored) = PhysicsStore.ReadStored(PhysicsStore.PathOf("walks", "city_stairs_up.tsv"));
        var lines = stored.TrimEnd('\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
            if (lines[i].Length > 0 && char.IsDigit(lines[i][0]) && int.Parse(lines[i].Split('\t')[0], Inv) >= 60)
                lines[i] = Edit(lines[i], c => c[3] = (D(c[3]) - 0.18).ToString("F5", Inv));
        string? diff = MovementReplayTests.OtherMaths.Compare(stored, string.Join('\n', lines) + "\n", out _);
        _o.WriteLine(diff);
        Assert.NotNull(diff);
    }
}

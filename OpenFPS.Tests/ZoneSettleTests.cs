using System.Numerics;
using OpenFPS.Client.Core;

namespace OpenFPS.Tests;

/// <summary>
/// A zone is said when you have crossed into it, not whenever the zone under you changes: it must
/// hold, with the body moving as a body moves, for <see cref="ZoneSettle.SettleSeconds"/>.
/// </summary>
public class ZoneSettleTests
{
    /// <summary>The announcer's rule, as ClientGameSession.AnnounceZoneChanges applies it: a settled
    /// change of zone id whose name differs from the last one said.</summary>
    private sealed class Announcer
    {
        private readonly ZoneSettle _settle = new();
        private int _lastId = int.MinValue;
        private string? _lastName;
        public readonly List<(double At, string Name)> Said = new();

        public void Update(double now, int zone, string name, Vector3 position)
        {
            bool settled = _settle.Update(zone, position, now);
            if (zone == _lastId || !settled) return;
            _lastId = zone;
            if (name == _lastName) return;
            _lastName = name;
            Said.Add((now, name));
        }
    }

    /// <summary>
    /// Cody's landing on Brandt Court's roof after a /tp to 100 m (2026-10-04), replayed from the client log:
    /// the position bounced up to 13 m over the roof and the zone between the roof's two boxes and none.
    /// Eighteen lines were said; the roof is said once, when he settles.
    /// </summary>
    [Fact]
    public void ABouncingLandingSaysTheRoofOnce()
    {
        const string roof = "Brandt Court roof", sidewalk = "sidewalk";
        var log = new (double T, int Zone, Vector3 At)[]
        {
            (59.862, 3364, new(-12f, 19.149f, 150f)), (59.889, -1, new(-12f, 30.232f, 150f)),
            (60.108, 3364, new(-12f, 19.149f, 150f)), (60.132, -1, new(-12f, 31.750f, 150f)),
            (60.412, 3364, new(-12f, 19.150f, 150f)), (60.554, 3362, new(-12f, 18.25f, 149.149f)),
            (60.596, -1, new(-12f, 25.582f, 150f)), (60.703, 3364, new(-12f, 19.149f, 150f)),
            (60.758, 3362, new(-12f, 18.25f, 149.149f)), (60.797, -1, new(-12f, 28.699f, 150f)),
            (61.041, 3364, new(-12f, 19.149f, 150f)), (61.052, 3362, new(-12f, 18.25f, 149.149f)),
            (61.165, -1, new(-12f, 27.150f, 150f)), (61.321, 3364, new(-12f, 19.150f, 150f)),
            (61.357, 3362, new(-12f, 18.25f, 149.149f)), (61.433, -1, new(-12f, 27.149f, 150f)),
            (61.655, 3364, new(-12f, 19.149f, 150f)), (61.681, 3362, new(-12f, 18.25f, 149.149f)),
            (61.870, 3364, new(-12f, 20.782f, 150f)), (61.940, 3362, new(-12f, 18.25f, 149.149f)),
            (62.092, -1, new(-12f, 25.583f, 150f)), (62.210, 3364, new(-12f, 19.150f, 150f)),
            (62.274, 3362, new(-12f, 18.25f, 149.149f)), (62.473, -1, new(-12f, 20.782f, 150f)),
            (62.805, 3364, new(-12f, 19.149f, 150f)), (62.841, -1, new(-12f, 18.25f, 149.149f)),
            (62.876, 3362, new(-12f, 18.25f, 149.149f)),
        };
        var a = new Announcer();
        // The last place said: the stairwell he had been standing in before the /tp.
        for (double t = 50.0; t < 51.0; t += 0.01) a.Update(t, 3103, "Brandt Court stairwell, floor 3", new(-12f, 8.7f, 150.9f));
        Assert.Single(a.Said);
        a.Said.Clear();
        int i = 0;
        for (double t = log[0].T; t < 66.0; t += 0.01)
        {
            while (i + 1 < log.Length && log[i + 1].T <= t) i++;
            var (_, zone, at) = log[i];
            a.Update(t, zone, zone == -1 ? sidewalk : roof, at);
        }
        Assert.Single(a.Said);
        Assert.Equal(roof, a.Said[0].Name);
        Assert.InRange(a.Said[0].At, 62.876, 62.876 + ZoneSettle.SettleSeconds + 0.02);
    }

    /// <summary>Walking through a doorway from one room into the next says the next room once, within
    /// the settling time of crossing; standing on the line, with the zone flickering between the two
    /// every tenth of a second, says nothing.</summary>
    [Fact]
    public void WalkingThroughADoorSaysTheRoomOnceAndStandingOnTheLineSaysNothing()
    {
        var a = new Announcer();
        const float speed = 4.5f;
        double crossed = double.NaN;
        for (double t = 0; t < 4.0; t += 1.0 / 60)
        {
            float x = -5f + speed * (float)t;
            if (x >= 0 && double.IsNaN(crossed)) crossed = t;
            a.Update(t, x < 0 ? 1 : 2, x < 0 ? "corridor" : "flat 3", new(x, 1.7f, 0f));
        }
        Assert.Equal(2, a.Said.Count);
        Assert.Equal("corridor", a.Said[0].Name);
        Assert.Equal("flat 3", a.Said[1].Name);
        Assert.InRange(a.Said[1].At - crossed, ZoneSettle.SettleSeconds - 0.02, ZoneSettle.SettleSeconds + 0.02);

        var still = new Announcer();
        for (double t = 0; t < 1.0; t += 1.0 / 60) still.Update(t, 2, "flat 3", new(0.5f, 1.7f, 0f));
        Assert.Single(still.Said);
        for (double t = 1.0; t < 6.0; t += 1.0 / 60)
            still.Update(t, ((int)(t * 10) % 2 == 0) ? 1 : 2, ((int)(t * 10) % 2 == 0) ? "corridor" : "flat 3", new(0f, 1.7f, 0f));
        Assert.Single(still.Said);
    }

    /// <summary>A body thrown about (corrected, carried, bouncing) is not settled while it jumps more than a
    /// metre an update, however long the zone under it holds.</summary>
    [Fact]
    public void AJumpingBodyIsNotSettled()
    {
        var s = new ZoneSettle();
        for (int k = 0; k < 120; k++)
            Assert.False(s.Update(5, new Vector3(0, k % 2 == 0 ? 18f : 20f, 0), k / 60.0));
        bool settled = false;
        for (int k = 120; k < 160; k++) settled = s.Update(5, new Vector3(0, 18f, 0), k / 60.0);
        Assert.True(settled);
    }
}

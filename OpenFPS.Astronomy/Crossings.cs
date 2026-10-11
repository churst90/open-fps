namespace OpenFPS.Astronomy;

/// <summary>When an altitude crosses a level: sampled, then halved to a second.</summary>
internal static class Crossings
{
    /// <summary>Ten minutes. A dip below the level shorter than this (the sun grazing the horizon near the
    /// polar circles) falls between samples, so a sample nearer the level than both its neighbours is
    /// searched for the turn and the turn tested.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan Resolution = TimeSpan.FromSeconds(1);

    /// <summary>The first time in [start, end) that <paramref name="altitude"/> passes <paramref name="level"/>
    /// going up (or down); null if it does not.</summary>
    public static DateTime? First(Func<DateTime, double> altitude, double level, DateTime start, DateTime end, bool rising)
    {
        double F(DateTime u) => altitude(u) - level;
        DateTime t0 = start, t1 = Min(start + Step, end);
        double f0 = F(t0), f1 = F(t1);
        // A crossing between t0 and t1 is found when the interval is first reached; a turn at t1 waits for t2.
        if (Crosses(f0, f1, rising)) return Done(Bisect(F, t0, t1, rising), end);
        while (t1 < end)
        {
            DateTime t2 = Min(t1 + Step, end);
            double f2 = F(t2);
            if (SameSide(f0, f1) && SameSide(f1, f2) && Math.Abs(f1) < Math.Abs(f0) && Math.Abs(f1) < Math.Abs(f2))
            {
                // f1 is the sample nearest the level: find the turn between t0 and t2 and see if it crosses.
                DateTime turn = Turn(F, t0, t2, towardBelow: f1 >= 0);
                double ft = F(turn);
                if (!SameSide(f1, ft))
                {
                    // Down then up, or up then down: the first of the two is in [t0, turn].
                    bool firstRising = f1 < 0;
                    if (firstRising == rising) return Done(Bisect(F, t0, turn, rising), end);
                    return Done(Bisect(F, turn, t2, rising), end);
                }
            }
            if (Crosses(f1, f2, rising)) return Done(Bisect(F, t1, t2, rising), end);
            t0 = t1; f0 = f1;
            t1 = t2; f1 = f2;
        }
        return null;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static bool SameSide(double a, double b) => (a >= 0) == (b >= 0);

    private static bool Crosses(double fa, double fb, bool rising) => rising ? fa < 0 && fb >= 0 : fa >= 0 && fb < 0;

    private static DateTime? Done(DateTime found, DateTime end) => found < end ? DateTime.SpecifyKind(found, DateTimeKind.Utc) : null;

    private static DateTime Bisect(Func<DateTime, double> f, DateTime lo, DateTime hi, bool rising)
    {
        while (hi - lo > Resolution)
        {
            DateTime mid = lo + (hi - lo) / 2;
            if ((f(mid) >= 0) == rising) hi = mid; else lo = mid;
        }
        return lo + (hi - lo) / 2;
    }

    /// <summary>The lowest (or highest) point of <paramref name="f"/> in [a, b], by golden section.</summary>
    private static DateTime Turn(Func<DateTime, double> f, DateTime a, DateTime b, bool towardBelow)
    {
        const double R = 0.6180339887498949;
        double sign = towardBelow ? 1 : -1;
        DateTime c = b - (b - a) * R, d = a + (b - a) * R;
        double fc = sign * f(c), fd = sign * f(d);
        while (b - a > Resolution)
        {
            if (fc < fd) { b = d; d = c; fd = fc; c = b - (b - a) * R; fc = sign * f(c); }
            else { a = c; c = d; fc = fd; d = a + (b - a) * R; fd = sign * f(d); }
        }
        return a + (b - a) / 2;
    }
}

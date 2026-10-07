using OpenFPS.Client.AudioEngine.Core.Signals;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// A struck bell's clapper rests on it for 12 ms after the blow, its extra loss holding the ring back
/// (StruckBell.Strike). The end of the blow's contact, a fifth of a millisecond in, used to lift the
/// loss again, so the hold did nothing (probable bug 5 of 2026-10-07).
/// </summary>
public class BellClapperHoldTests
{
    private readonly ITestOutputHelper _o;
    public BellClapperHoldTests(ITestOutputHelper o) => _o = o;

    private const int Rate = 48000;

    /// <summary>The energy of one blow between two times, in milliseconds after it.</summary>
    private static double[] Windows(StruckBellSpec spec, params (double From, double To)[] windows)
    {
        var bell = new StruckBell(spec, Rate);
        bell.Strike();
        var e = new double[windows.Length];
        double last = windows.Max(w => w.To);
        for (int i = 0; i * 1000.0 / Rate < last; i++)
        {
            bell.Step();
            double ms = i * 1000.0 / Rate;
            for (int k = 0; k < windows.Length; k++)
                if (ms >= windows[k].From && ms < windows[k].To) e[k] += bell.Out * (double)bell.Out;
        }
        return e;
    }

    private static double Db(double a, double b) => 10 * Math.Log10(a / b);

    /// <summary>
    /// Held, the fast top modes that carry the first milliseconds lose more while the clapper is on:
    /// the first 11 ms fall against the next 10 ms by a different amount with the damping than without.
    /// Before the fix the two differed by half a decibel on the tram gong; the hold now takes 2.4.
    /// </summary>
    [Fact]
    public void TheClapperDampsTheRingUntilItComesOff()
    {
        var tram = StruckBellSpec.TramGong;
        var held = Windows(tram, (1, 11), (13, 23));
        var free = Windows(tram with { ClapperDamping = 0f }, (1, 11), (13, 23));
        double h = Db(held[0], held[1]), f = Db(free[0], free[1]);
        _o.WriteLine($"tram gong, 1-11 ms over 13-23 ms: {h:F2} dB held, {f:F2} dB free");
        Assert.True(Math.Abs(h - f) > 1.0, $"the clapper changed the first 12 ms by {h - f:F2} dB");
    }

    /// <summary>
    /// Once it is off, the ring is the bell's own: from 50 to 250 ms it dies within a decibel or so of the
    /// undamped bell's (measured 1.2: the hold took more of the fast modes, so fewer are left to die). A
    /// hold that never lifted would lose tens of decibels more here.
    /// </summary>
    [Fact]
    public void OffTheBellTheRingDiesAtItsOwnRate()
    {
        var tram = StruckBellSpec.TramGong;
        var held = Windows(tram, (50, 100), (200, 250));
        var free = Windows(tram with { ClapperDamping = 0f }, (50, 100), (200, 250));
        double h = Db(held[0], held[1]), f = Db(free[0], free[1]);
        _o.WriteLine($"50-100 ms over 200-250 ms: {h:F2} dB held, {f:F2} dB free");
        Assert.InRange(h - f, -3.0, 3.0);
    }
}

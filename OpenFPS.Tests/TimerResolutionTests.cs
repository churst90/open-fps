using OpenFPS.Client.Core.Platform;

namespace OpenFPS.Tests;

/// <summary>
/// The 1 ms timer the Windows client asks for (Sean's log, 2026-10-05: the 250 Hz audio thread at
/// 59 Hz and the game loop at 63, both held to Windows' 15.6 ms tick). What it asks for, and that off
/// Windows it asks for nothing and changes nothing.
/// </summary>
public class TimerResolutionTests
{
    [Theory]
    [InlineData(1u, 1u, 1_000_000u, 1u)]     // the usual machine: 1 ms is on offer
    [InlineData(1u, 2u, 1_000_000u, 2u)]     // a machine whose finest is 2 ms gets 2
    [InlineData(0u, 1u, 1_000_000u, 1u)]     // never "0 ms", which timeBeginPeriod refuses
    [InlineData(1u, 0u, 0u, 1u)]             // caps that could not be read: ask for what was wanted
    [InlineData(5u, 1u, 3u, 3u)]             // never coarser than the coarsest the machine has
    public void ThePeriodIsWhatWasWantedWithinWhatTheMachineCanDo(uint wanted, uint min, uint max, uint chosen)
        => Assert.Equal(chosen, TimerResolution.Choose(wanted, min, max));

    /// <summary>Linux sleeps to the microsecond already: nothing is asked for, and letting it go is safe,
    /// twice over.</summary>
    [Fact]
    public void OffWindowsNothingIsAskedFor()
    {
        if (OperatingSystem.IsWindows()) return;
        var timer = TimerResolution.Raise();
        Assert.False(timer.Active);
        Assert.Equal(0u, timer.PeriodMs);
        timer.Dispose();
        timer.Dispose();
    }

    /// <summary>The figure the Windows log line reports, and what it reads where sleeps are fine: a
    /// 1 ms sleep well under the 15.6 ms Windows tick that held the audio thread to 59 Hz.</summary>
    [Fact]
    public void AOneMillisecondSleepIsMeasured()
    {
        double ms = TimerResolution.MeasureSleepMs(1, 10);
        Assert.InRange(ms, 0.9, 1000);
        if (OperatingSystem.IsLinux()) Assert.True(ms < 10, $"a 1 ms sleep took {ms:F1} ms");
    }
}

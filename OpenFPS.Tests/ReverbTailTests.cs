using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// The reverb unit renders a tail. FMOD's EARLYLATEMIX blends late reverb to early reflections, 0 all early
/// and 100 all late; the project wants the unit's stamped early reflections off (geometry places its own),
/// so 100. At 0 every room's surveyed decay reached nothing: measured with AudioLab --tailcheck on a step in
/// a six-second room, the mix was at the noise floor 500 ms later, against 28 dB up at two seconds at 100.
/// </summary>
public class ReverbTailTests
{
    [Fact]
    public void TheUnitRendersTheTailAndNotItsOwnEarlyReflections()
    {
        // 100 is all late reverb: the tail and none of the unit's stamped copies; 0 removes the tail.
        Assert.Equal(100.0f, AcousticConstants.ReverbLateToEarlyMixPercent);
    }

    /// <summary>A room's decay reaches the unit unclamped, or the city's rooms all arrive at one ceiling and
    /// sound alike.</summary>
    [Fact]
    public void TheDecayRangeCoversTheRoomsTheCityHas()
    {
        // The garage surveys at about five seconds and the stairwell at two; both must fit.
        Assert.True(AcousticConstants.MaxReverbDecayMs >= 5200f,
            $"a car park surveys past five seconds and the ceiling is {AcousticConstants.MaxReverbDecayMs} ms");
        Assert.True(AcousticConstants.MinReverbDecayMs <= 300f,
            $"a small carpeted room is under a third of a second and the floor is {AcousticConstants.MinReverbDecayMs} ms");
    }
}

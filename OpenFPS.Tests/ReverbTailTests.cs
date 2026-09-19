using OpenFPS.Common;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// The reverb unit has to render a TAIL, and the parameter that decides whether it does reads
/// backwards.
///
/// FMOD's EARLYLATEMIX is the blend of LATE REVERB TO EARLY REFLECTIONS: 0 is all early, 100 is all
/// late. The project wants the unit's synthetic early reflections off — they are a fixed pattern
/// stamped on every transient, and geometry's image-source pass answers that question per source — so
/// the value it wants is 100. It was 0, which asks for early reflections and NOTHING ELSE.
///
/// The cost of that one number was a session of listening: every room's decay was surveyed correctly,
/// written to its own bus correctly, and fed correctly, and none of it reached the ear, because the
/// unit had been told to render no tail. Measured with AudioLab --tailcheck on a footstep in a room
/// configured for six seconds of decay: at 0 the mix is at the noise floor 500 ms later; at 100 it is
/// still 28 dB up at two seconds.
/// </summary>
public class ReverbTailTests
{
    [Fact]
    public void TheUnitRendersTheTailAndNotItsOwnEarlyReflections()
    {
        // 100 on FMOD's scale is "all late reverb": the tail, and none of the unit's stamped copies.
        // Anything less than fully late lets the unit's early reflections back in; 0 removes the tail
        // altogether, which is the fault this pins.
        Assert.Equal(100.0f, AcousticConstants.ReverbLateToEarlyMixPercent);
    }

    /// <summary>
    /// A room's decay has to be able to reach the unit unclamped, or the rooms a city is made of all
    /// arrive at the same ceiling and sound alike for a second reason.
    /// </summary>
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

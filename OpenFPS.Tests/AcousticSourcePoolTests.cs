using OpenFPS.Client.AudioEngine.Acoustics;

namespace OpenFPS.Tests;

/// <summary>
/// The Steam Audio source pool (64) filled on the city with every id asked about in the last five
/// seconds, and the pool was first come, first served: the newest sound was refused and fell back to
/// the hand-rolled tracer. A full pool now lends out the source that has gone longest unasked — and
/// never one that is being asked about this tick.
/// </summary>
public class AcousticSourcePoolTests
{
    [Fact]
    public void Reclaims_the_longest_unasked_source()
    {
        var held = new Dictionary<int, nint> { [1] = 10, [2] = 20, [3] = 30 };
        var seen = new Dictionary<int, long> { [1] = 500, [2] = 100, [3] = 900 };
        var wanted = new Dictionary<int, int> { [9] = 0 };
        Assert.Equal(2, AsyncAcousticWorker.PickSourceToReclaim(held, seen, wanted));
    }

    [Fact]
    public void Never_takes_a_source_wanted_this_tick()
    {
        var held = new Dictionary<int, nint> { [1] = 10, [2] = 20, [3] = 30 };
        var seen = new Dictionary<int, long> { [1] = 500, [2] = 100, [3] = 900 };
        var wanted = new Dictionary<int, int> { [2] = 0, [9] = 0 };
        Assert.Equal(1, AsyncAcousticWorker.PickSourceToReclaim(held, seen, wanted));
    }

    [Fact]
    public void Nothing_to_reclaim_when_every_held_source_is_wanted()
    {
        var held = new Dictionary<int, nint> { [1] = 10, [2] = 20 };
        var seen = new Dictionary<int, long> { [1] = 500, [2] = 100 };
        var wanted = new Dictionary<int, int> { [1] = 0, [2] = 0, [9] = 0 };
        Assert.Equal(int.MinValue, AsyncAcousticWorker.PickSourceToReclaim(held, seen, wanted));
    }
}

using OpenFPS.Client.AudioEngine.Acoustics;

namespace OpenFPS.Tests;

/// <summary>
/// The Steam Audio source pool (64) fills on the city with every id asked about in the last five
/// seconds. A full pool lends out the source that has gone longest unasked, never one asked about this
/// tick; first come, first served, the newest sound was refused and fell back to the hand-rolled tracer.
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

    /// <summary>
    /// Eighty sources asked about every tick and a pool of 64 (--pop-hunt extra=40, 2026-10-03): the line
    /// starts with whoever was turned away last tick, the first 64 are served, and a source held by
    /// someone further back is lent forward. Otherwise the same sixteen are refused every tick and cars
    /// behind a building pop between -63 and -15 dB through the hand-rolled tracer. Run as the worker runs
    /// it: the same helpers, the same order.
    /// </summary>
    [Fact]
    public void Nobody_is_turned_away_two_ticks_running()
    {
        const int capacity = 64, asked = 80;
        var held = new Dictionary<int, nint>();
        var seen = new Dictionary<int, long>();
        var pending = new Dictionary<int, int>();
        var carried = new List<int>();
        var served = new HashSet<int>();
        var refusedLastTick = new HashSet<int>();
        nint next = 1;
        for (int tick = 0; tick < 12; tick++)
        {
            // WorkerLoop: last tick's refused first, then everything asked since.
            pending.Clear();
            foreach (int id in carried) pending[id] = 0;
            for (int id = 0; id < asked; id++) pending[id] = 0;
            AsyncAcousticWorker.Serve(pending.Keys, capacity, served);
            // RunSteamAudio: a source for each served id, from the pool or lent by someone not served.
            foreach (int id in pending.Keys)
            {
                if (!served.Contains(id) || held.ContainsKey(id)) continue;
                if (held.Count < capacity) { held[id] = next++; }
                else
                {
                    int victim = AsyncAcousticWorker.PickSourceToReclaim(held, seen, served);
                    Assert.NotEqual(int.MinValue, victim);
                    held[id] = held[victim];
                    held.Remove(victim);
                }
                seen[id] = tick;
            }
            foreach (int id in served) seen[id] = tick;
            Assert.All(served, id => Assert.True(held.ContainsKey(id), $"tick {tick}: {id} served without a source"));
            carried = pending.Keys.Where(id => !served.Contains(id)).ToList();
            Assert.Empty(carried.Where(refusedLastTick.Contains));
            refusedLastTick = carried.ToHashSet();
        }
    }

    [Fact]
    public void A_source_held_by_someone_not_served_this_tick_is_lent()
    {
        var held = new Dictionary<int, nint> { [1] = 10, [2] = 20 };
        var seen = new Dictionary<int, long> { [1] = 500, [2] = 100 };
        // Both asked about this tick, but only 1 and 9 have a place.
        var served = new HashSet<int> { 1, 9 };
        Assert.Equal(2, AsyncAcousticWorker.PickSourceToReclaim(held, seen, served));
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

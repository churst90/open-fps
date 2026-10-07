using System.Diagnostics;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// The render pool's workers share one sweep down the voices (EngineRenderPool.Work). With a fixed share
/// each, a worker held by one slow voice (a train's tap waiting on its train's lock, 2026-10-07) starved
/// every voice in its share while the other workers had time.
/// </summary>
public class EngineRenderPoolTests
{
    private readonly ITestOutputHelper _o;
    public EngineRenderPoolTests(ITestOutputHelper o) => _o = o;

    private sealed class Fake : IRenderedVoice
    {
        private readonly int _sleepMs;
        private readonly Stopwatch _clock;
        private int _busy;
        public long LastAt = -1, WorstGap;
        public Fake(int sleepMs, Stopwatch clock) { _sleepMs = sleepMs; _clock = clock; }
        public void Produce()
        {
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            long now = _clock.ElapsedMilliseconds;
            if (LastAt >= 0 && now > 300) WorstGap = Math.Max(WorstGap, now - LastAt);
            LastAt = now;
            if (_sleepMs > 0) Thread.Sleep(_sleepMs);
            Volatile.Write(ref _busy, 0);
        }
    }

    [Fact]
    public void A_slow_voice_holds_up_only_itself()
    {
        var clock = Stopwatch.StartNew();
        var slow = new Fake(1000, clock);
        var fast = Enumerable.Range(0, 24).Select(_ => new Fake(0, clock)).ToArray();
        var list = new List<IRenderedVoice> { slow };
        list.AddRange(fast);
        using (var pool = new EngineRenderPool(() => new List<IRenderedVoice>(list)))
            Thread.Sleep(2500);
        long worst = fast.Max(f => f.WorstGap);
        _o.WriteLine($"the longest any quick voice waited for a worker: {worst} ms, with one voice taking 1000 ms a pass");
        Assert.True(worst < 400, $"a quick voice waited {worst} ms behind the slow one");
    }
}

using Serilog;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Renders every live engine ahead of the mixer, across cores. One engine is a serial integration
/// and cannot be split, but thirty engines are thirty independent ones; each fills its own ring from
/// a worker and the mixer callback only copies out of it. Rendered inside the FMOD callback, all of
/// them ran on one core against the mixer's deadline. See docs/CLIENT_NOTES.md, "Engine render pool".
/// </summary>
public sealed class EngineRenderPool : IDisposable
{
    private readonly Func<List<IRenderedVoice>> _snapshot;
    private readonly Thread[] _workers;
    private readonly Thread _coordinator;
    private volatile bool _running = true;

    /// <summary>
    /// The voices to render, republished as a whole array, never mutated: workers read the reference
    /// without a lock. If it is swapped between two workers' reads, two may reach for one voice and the
    /// second finds its producer already claimed. Sorted nearest first, so when the machine cannot fill
    /// every ring in time the cars it fills are the ones you can hear.
    /// </summary>
    private volatile IRenderedVoice[] _voices = Array.Empty<IRenderedVoice>();

    public EngineRenderPool(Func<List<IRenderedVoice>> snapshot)
    {
        _snapshot = snapshot;

        // Dedicated threads, never the .NET thread pool: at a map load the pool is saturated by the
        // acoustic bake, the scene build and the sample decodes, and on Parallel.For the producers
        // starved exactly then (audio choppy for the first seconds in a map).
        // Half the machine, up to twelve: with forty engines on the city at 0.1-0.23 of a core each
        // (--tap-balance cost), six threads were short and the nearest cars starved. ProcessorCount is the
        // machine's unless DOTNET_PROCESSOR_COUNT says otherwise: the build and test scripts set it, the
        // client's start scripts do not (24 cores, 12 workers, on Cody's 2026-10-07 log).
        int count = Math.Clamp(Environment.ProcessorCount / 2, 2, 12);
        _workers = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            int id = i;
            _workers[i] = new Thread(() => Work(id, count))
            {
                IsBackground = true,
                // Windows only. On Linux raising a priority needs CAP_SYS_NICE and CoreCLR ignores it
                // silently; what works there is lowering the loader's threads, done where they start.
                // Nothing about this pool's behaviour is explained by this line (docs/AUDIO_LOAD_DROPOUTS.md).
                Priority = ThreadPriority.Highest,
                Name = $"EngineRender{id}",
            };
            _workers[i].Start();
        }

        _coordinator = new Thread(Refresh)
        {
            IsBackground = true,
            Priority = ThreadPriority.Normal,
            Name = "EngineRenderList",
        };
        _coordinator.Start();

        Log.Information("Engine rendering moved off the mixer: {Threads} dedicated thread(s) of {Cores} cores, {Lead:F0}-{Max:F0} ms ahead.",
                        count, Environment.ProcessorCount,
                        EngineVoiceState.MinLeadSeconds * 1000f, EngineVoiceState.MaxLeadSeconds * 1000f);
    }

    /// <summary>Republishes the voice list. Separate from the workers so taking the provider's lock
    /// can never stall one of them mid-render.</summary>
    private void Refresh()
    {
        while (_running)
        {
            try { _voices = _snapshot().ToArray(); }
            catch (Exception ex) { Log.Error(ex, "EngineRenderPool: could not list the live engines."); }
            Thread.Sleep(20);
        }
    }

    /// <summary>The next voice of the current sweep a worker may take.</summary>
    private int _next;
    /// <summary>Stopwatch ticks the workers spent rendering, for <see cref="TakeBusy"/>.</summary>
    private long _busyTicks;
    private long _busySince = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>
    /// Every worker takes the next voice of one shared sweep down the list, nearest first, rather than a
    /// fixed share of it. With fixed shares a worker held up by one slow voice (a train's tap waiting on its
    /// train's lock, on 2026-10-07) starved every other voice of its share while eleven workers had time;
    /// now the others take them. And when the pool cannot fill every ring in time, the voices at the end of
    /// the list, the furthest, are the ones that starve, and the budget gives those up (ChooseLiveEngines).
    /// </summary>
    private void Work(int id, int stride)
    {
        while (_running)
        {
            var voices = _voices;
            try
            {
                for (int i = Interlocked.Increment(ref _next) - 1; i < voices.Length; i = Interlocked.Increment(ref _next) - 1)
                {
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    voices[i].Produce();
                    Interlocked.Add(ref _busyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "EngineRenderPool: error while rendering ahead.");
            }
            // The sweep is done: the next starts from the top. Two workers may both start it; a voice
            // already being rendered is passed over (Produce's own guard).
            if (Volatile.Read(ref _next) >= voices.Length) Interlocked.Exchange(ref _next, 0);
            // Short enough to stay well ahead of the lead, long enough not to spin a core.
            Thread.Sleep(2);
        }
    }

    /// <summary>The share of the workers' time spent rendering since the last call, 0 to 1, smoothed over
    /// a few calls: a long render is counted when it ends, all in one interval. For the log.</summary>
    public float TakeBusy()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long busy = Interlocked.Exchange(ref _busyTicks, 0);
        long span = now - Interlocked.Exchange(ref _busySince, now);
        float share = span > 0 ? (float)busy / (span * (float)_workers.Length) : 0f;
        _busySmoothed += (share - _busySmoothed) * 0.3f;
        return _busySmoothed;
    }
    private float _busySmoothed;

    public void Dispose()
    {
        _running = false;
        foreach (var t in _workers) t.Join(500);
        _coordinator.Join(500);
    }
}

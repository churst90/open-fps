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
        // (--tap-balance cost), six threads were short and the nearest cars starved.
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

    private void Work(int id, int stride)
    {
        while (_running)
        {
            var voices = _voices;
            try
            {
                for (int i = id; i < voices.Length; i += stride) voices[i].Produce();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "EngineRenderPool: error while rendering ahead.");
            }
            // Short enough to stay well ahead of the lead, long enough not to spin a core.
            Thread.Sleep(2);
        }
    }

    public void Dispose()
    {
        _running = false;
        foreach (var t in _workers) t.Join(500);
        _coordinator.Join(500);
    }
}

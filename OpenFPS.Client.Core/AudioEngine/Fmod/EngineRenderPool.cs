using System;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using Serilog;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// Renders every live engine AHEAD of the mixer, across as many cores as the machine has.
///
/// This is the change that removes the ceiling on how many vehicles a map may carry. A physical
/// engine is a serial integration — each sample depends on the one before it — so a single engine
/// cannot be split across threads. But thirty engines are thirty INDEPENDENT integrations, and
/// running them one after another inside the FMOD callback, which is what happened before, meant a
/// twenty-four core machine did all of it on one core with the mixer's deadline running down. Every
/// symptom that followed — the rationing, the shedding, the borrowed voices, cars dropping out of
/// the world — traces back to that one thread.
///
/// The work is the same; only where it happens changes. Each engine fills its own ring buffer from
/// a worker, and the mixer callback copies out of it. The ring already existed, because echoes read
/// back through it; this makes the engine's own voice read through it too.
///
/// Why not the GPU: the sample axis is a recurrence and so cannot be parallelised at all, the valve
/// solver's iteration count is data-dependent (which a lockstep warp pays for at its worst lane),
/// and the GPU is not real-time scheduled — a graphics hitch would become an audio dropout. Thirty
/// independent lanes is a poor fit for a device that wants thousands and an excellent fit for a CPU
/// that has twenty-four.
/// </summary>
public sealed class EngineRenderPool : IDisposable
{
    private readonly Func<List<EngineVoiceState>> _snapshot;
    private readonly Thread[] _workers;
    private readonly Thread _coordinator;
    private volatile bool _running = true;

    /// <summary>
    /// The voices to render, republished as a whole array rather than mutated in place.
    ///
    /// Workers only ever read the reference, so there is no lock on the hot path and no list for a
    /// worker to walk while it is being changed underneath. Each worker takes every Nth entry; if the
    /// array is swapped between one worker reading it and another, the worst case is that two workers
    /// reach for the same voice, and the second finds the producer already claimed and moves on.
    ///
    /// The list arrives sorted NEAREST FIRST, and the stride preserves that: when the machine cannot
    /// fill every ring in time, the cars that get filled are the ones you can hear.
    /// </summary>
    private volatile EngineVoiceState[] _voices = Array.Empty<EngineVoiceState>();

    /// <summary>Blocks the mixer asked for that no producer had rendered yet, since the client
    /// started. The mixer no longer finishes those blocks itself — it ramps out and reports.</summary>
    public int Underruns => EngineVoiceState.GlobalStarves;

    public EngineRenderPool(Func<List<EngineVoiceState>> snapshot)
    {
        _snapshot = snapshot;

        // DEDICATED threads, not the shared thread pool, and this is the whole point of the class.
        //
        // The first version used Parallel.For, which runs on the .NET thread pool — the same pool
        // that, at the exact moment a map loads, is saturated by the acoustic bake, the Steam Audio
        // scene build and several dozen sample decodes. The pool grows by a thread or two a second
        // when it is starved, so the engine producers got no time precisely when thirty of them had
        // just been created, fell behind, and dumped the whole load back onto the mixer callback.
        // Heard as the audio cutting out and going choppy for the first seconds in the map.
        //
        // A real-time producer cannot share a scheduler with background work. These threads exist for
        // the life of the engine, are owned here, and nothing else can take them.
        int count = Math.Clamp(Environment.ProcessorCount / 4, 2, 6);
        _workers = new Thread[count];
        for (int i = 0; i < count; i++)
        {
            int id = i;
            _workers[i] = new Thread(() => Work(id, count))
            {
                IsBackground = true,
                // Windows only, and worth almost nothing even there. CoreCLR on Unix ACCEPTS this
                // and silently does not apply it: raising a thread's priority needs CAP_SYS_NICE,
                // which a game does not have, so on Linux the render workers, the acoustic bake, the
                // sample decodes, the JIT and FMOD's own mixer thread all run at the same priority.
                // What DOES work unprivileged is lowering the loader's threads, which is done where
                // that work is started — not here. Nothing about this pool's behaviour should ever
                // be explained by this line.
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

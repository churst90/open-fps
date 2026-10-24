namespace OpenFPS.Audio.Core;

/// <summary>
/// What an audio world's components have running in the background, so the world's dispose can wait for it:
/// a task or a thread is started through here and found again by name. Work that has nothing to wait for
/// (<c>on</c> null: a component made by itself, in a test) starts as before.
/// </summary>
internal sealed class BackgroundWork
{
    private readonly object _gate = new();
    private readonly List<(string Name, Task? Task, Thread? Thread)> _items = new();

    /// <summary>Starts <paramref name="work"/> on the thread pool, kept by <paramref name="on"/> if there is one.</summary>
    public static Task<T> Run<T>(BackgroundWork? on, string name, Func<T> work)
    {
        var task = Task.Run(work);
        on?.Track(name, task);
        return task;
    }

    /// <summary>Starts <paramref name="work"/> on the thread pool, kept by <paramref name="on"/> if there is one.</summary>
    public static Task Run(BackgroundWork? on, string name, Action work)
    {
        var task = Task.Run(work);
        on?.Track(name, task);
        return task;
    }

    /// <summary>Keeps a task started elsewhere (a scheduler of its own) for the dispose to wait on.</summary>
    public void Track(string name, Task task)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Task is { IsCompleted: true } || i.Thread is { IsAlive: false });
            if (!task.IsCompleted) _items.Add((name, task, null));
        }
    }

    /// <summary>Keeps a thread started elsewhere for the dispose to join.</summary>
    public void Track(string name, Thread thread)
    {
        lock (_gate)
        {
            _items.RemoveAll(i => i.Task is { IsCompleted: true } || i.Thread is { IsAlive: false });
            _items.Add((name, null, thread));
        }
    }

    /// <summary>How many tasks and threads are still running.</summary>
    public int Running
    {
        get
        {
            lock (_gate) return _items.Count(i => i.Task is { IsCompleted: false } || i.Thread is { IsAlive: true });
        }
    }

    /// <summary>Waits up to <paramref name="ms"/> in all for everything kept to finish, including what is kept while
    /// waiting. Returns the names of what did not.</summary>
    public List<string> Wait(int ms)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            (string Name, Task? Task, Thread? Thread)[] now;
            lock (_gate)
            {
                _items.RemoveAll(i => i.Task is { IsCompleted: true } || i.Thread is { IsAlive: false });
                now = _items.ToArray();
            }
            if (now.Length == 0) return new List<string>();
            foreach (var item in now)
            {
                int left = (int)Math.Max(0, ms - clock.ElapsedMilliseconds);
                try
                {
                    if (item.Task != null) item.Task.Wait(left);
                    else if (item.Thread != Thread.CurrentThread) item.Thread!.Join(left);
                }
                // A task that failed is a finished one; its owner logged what it could.
                catch (AggregateException) { }
            }
            if (clock.ElapsedMilliseconds >= ms)
            {
                lock (_gate)
                    return _items.Where(i => i.Task is { IsCompleted: false } || i.Thread is { IsAlive: true })
                                 .Select(i => i.Name).Distinct().ToList();
            }
        }
    }
}

/// <summary>A part of the audio that holds threads or native objects, stopped and freed in two steps by the
/// facade's dispose.</summary>
internal interface IAudioShutdown
{
    string ShutdownName { get; }

    /// <summary>Stops new work and waits up to <paramref name="waitMs"/> for the threads and tasks running. True when
    /// every one stopped. Nothing is freed yet: the mixer may still be reading it.</summary>
    bool Stop(int waitMs);

    /// <summary>Frees what the part owns, once the mixer has stopped. Frees nothing a thread that did not stop could
    /// still be using.</summary>
    void Free();
}

/// <summary>Bounded waits for the dispose, each saying by name what it could not stop.</summary>
internal static class Shutdown
{
    private static readonly List<WeakReference<Thread>> Threads = new();

    /// <summary>Notes a thread the audio started, so a test (and a host's log) can see which are still alive.</summary>
    public static void Started(Thread thread)
    {
        lock (Threads)
        {
            Threads.RemoveAll(r => !r.TryGetTarget(out var t) || (t.ThreadState & ThreadState.Stopped) != 0);
            Threads.Add(new WeakReference<Thread>(thread));
        }
    }

    /// <summary>The audio's threads that are still alive, any world or facade.</summary>
    public static List<Thread> Alive()
    {
        lock (Threads)
            return Threads.Select(r => r.TryGetTarget(out var t) ? t : null)
                          .Where(t => t is { IsAlive: true }).Select(t => t!).ToList();
    }

    /// <summary>Waits up to <paramref name="ms"/> for a thread; logs it by name when it does not stop.</summary>
    public static bool Join(Thread? thread, string name, int ms)
    {
        if (thread == null || !thread.IsAlive || thread == Thread.CurrentThread) return true;
        if (thread.Join(ms)) return true;
        Serilog.Log.Warning("Audio shutdown: {Name} did not stop within {Ms} ms.", name, ms);
        return false;
    }

    /// <summary>Waits up to <paramref name="ms"/> for a task; logs it by name when it does not finish.</summary>
    public static bool Wait(Task? task, string name, int ms)
    {
        if (task == null || task.IsCompleted) return true;
        try { if (task.Wait(ms)) return true; }
        catch (AggregateException) { return true; }
        Serilog.Log.Warning("Audio shutdown: {Name} did not finish within {Ms} ms.", name, ms);
        return false;
    }
}

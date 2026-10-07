using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace OpenFPS.Common;

/// <summary>
/// The measuring half of "profile, then cut the hot paths": named timers and counters that any thread can
/// write to, and a periodic report of what a frame actually spent.
///
/// It is off unless <c>OPENFPS_PROFILE=1</c>, and when off every entry point here is a static bool test and
/// a return. That is the whole point of it being permanent rather than a scaffold added and removed around
/// each investigation: the probes can live in the hot paths, so the next time something is slow the numbers
/// are one environment variable away instead of a re-instrumentation exercise.
///
/// Deliberately NOT a general profiler — no call tree, no sampling, no allocation tracking. It answers one
/// question, how long does this named thing take and how often does it happen, because that is the question
/// the audit's remaining performance items ask.
/// </summary>
public static class PerfProbe
{
    /// <summary>On when <c>OPENFPS_PROFILE=1</c>. Settable so a test can drive the probe directly.</summary>
    public static bool Enabled { get; set; } = Environment.GetEnvironmentVariable("OPENFPS_PROFILE") == "1";

    private sealed class Entry
    {
        public long Count;
        public long TotalTicks;
        public long MaxTicks;
        public bool IsTimer;
    }

    private static readonly ConcurrentDictionary<string, Entry> _entries = new();
    private static long _lastReportStamp = Stopwatch.GetTimestamp();
    private static readonly double _msPerTick = 1000.0 / Stopwatch.Frequency;

    /// <summary>Adds to a plain counter: calls, sources, rays, snapshot builds — anything that is a number
    /// per reporting window rather than a duration.</summary>
    public static void Count(string name, long amount = 1)
    {
        if (!Enabled) return;
        var e = _entries.GetOrAdd(name, static _ => new Entry());
        Interlocked.Add(ref e.Count, amount);
    }

    /// <summary>
    /// Times the enclosing block: <c>using (PerfProbe.Measure("audio.update")) { ... }</c>.
    /// The returned scope is inert when profiling is off, so the using-block costs nothing.
    /// </summary>
    public static Scope Measure(string name) => Enabled ? new Scope(name, Stopwatch.GetTimestamp()) : default;

    /// <summary>Records one duration that the caller timed itself, in <see cref="Stopwatch"/> ticks.</summary>
    public static void Record(string name, long elapsedTicks)
    {
        if (!Enabled) return;
        var e = _entries.GetOrAdd(name, static _ => new Entry());
        e.IsTimer = true;
        Interlocked.Increment(ref e.Count);
        Interlocked.Add(ref e.TotalTicks, elapsedTicks);

        long max = Interlocked.Read(ref e.MaxTicks);
        while (elapsedTicks > max)
        {
            long seen = Interlocked.CompareExchange(ref e.MaxTicks, elapsedTicks, max);
            if (seen == max) break;
            max = seen;
        }
    }

    /// <summary>The scope returned by <see cref="Measure"/>. A default instance does nothing.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;

        internal Scope(string name, long start)
        {
            _name = name;
            _start = start;
        }

        public void Dispose()
        {
            if (_name is null) return;
            Record(_name, Stopwatch.GetTimestamp() - _start);
        }
    }

    /// <summary>
    /// Formats everything collected since the last report and resets the counters, so each report covers
    /// one window rather than all of history — an average over a whole session hides the frame that hitched.
    /// Returns an empty list when nothing was recorded.
    /// </summary>
    public static List<string> Drain()
    {
        var lines = new List<string>();
        foreach (var name in SortedNames())
        {
            if (!_entries.TryGetValue(name, out var e)) continue;

            long count = Interlocked.Exchange(ref e.Count, 0);
            long total = Interlocked.Exchange(ref e.TotalTicks, 0);
            long max = Interlocked.Exchange(ref e.MaxTicks, 0);
            if (count == 0) continue;

            if (e.IsTimer)
            {
                double avgMs = total * _msPerTick / count;
                double maxMs = max * _msPerTick;
                lines.Add($"{name,-34} n={count,-7} avg={avgMs,7:F3}ms max={maxMs,7:F3}ms");
            }
            else
            {
                lines.Add($"{name,-34} n={count}");
            }
        }
        return lines;
    }

    private static List<string> SortedNames()
    {
        var names = new List<string>(_entries.Keys);
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>
    /// Emits a report through <paramref name="sink"/> at most once per <paramref name="interval"/>.
    /// Safe (and cheap) to call every frame; does nothing when profiling is off.
    /// </summary>
    public static void ReportIfDue(TimeSpan interval, Action<string> sink)
    {
        if (!Enabled) return;

        long now = Stopwatch.GetTimestamp();
        long last = Interlocked.Read(ref _lastReportStamp);
        if ((now - last) * _msPerTick < interval.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _lastReportStamp, now, last) != last) return;

        var lines = Drain();
        if (lines.Count == 0) return;

        var sb = new StringBuilder();
        sb.Append("[perf] ").Append(lines.Count).Append(" counters over the last ")
          .Append(interval.TotalSeconds.ToString("F0")).AppendLine("s:");
        foreach (var line in lines) sb.Append("[perf]   ").AppendLine(line);
        sink(sb.ToString().TrimEnd());
    }

    /// <summary>Forgets everything measured so far. For tests.</summary>
    public static void Reset()
    {
        _entries.Clear();
        Interlocked.Exchange(ref _lastReportStamp, Stopwatch.GetTimestamp());
    }
}

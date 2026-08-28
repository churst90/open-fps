using System;
using System.Collections.Concurrent;

namespace OpenFPS.Server.Core;

/// <summary>
/// A token bucket per key, used to put a ceiling on how often one source may attempt an expensive or
/// security-sensitive operation.
///
/// Login and registration are both: each verifies a bcrypt hash (deliberately slow) and each is a
/// credential guess. Unthrottled, one host can both mine the account list and occupy the whole tick
/// thread doing it, because every UDP message is handled inline on that thread. The bucket is keyed by
/// remote address rather than by connection id — a connection id is free to churn, an address is not.
/// </summary>
public sealed class RateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastRefillMs;
    }

    private readonly int _capacity;
    private readonly double _refillPerMs;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private long _lastPruneMs = Environment.TickCount64;

    /// <param name="capacity">Attempts allowed in a burst.</param>
    /// <param name="refillPerSecond">Sustained attempts per second once the burst is spent.</param>
    public RateLimiter(int capacity, double refillPerSecond)
    {
        _capacity = Math.Max(1, capacity);
        _refillPerMs = refillPerSecond / 1000.0;
    }

    /// <summary>Takes one token for <paramref name="key"/>. False means the caller is over its limit.</summary>
    public bool TryConsume(string key)
    {
        long now = Environment.TickCount64;
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = _capacity, LastRefillMs = now });

        lock (bucket)
        {
            bucket.Tokens = Math.Min(_capacity, bucket.Tokens + (now - bucket.LastRefillMs) * _refillPerMs);
            bucket.LastRefillMs = now;

            if (bucket.Tokens < 1.0) return false;
            bucket.Tokens -= 1.0;
        }

        Prune(now);
        return true;
    }

    /// <summary>
    /// Drops buckets that have refilled to full, so a spray of one-shot attempts from many addresses
    /// cannot grow the dictionary without bound. Runs at most once a minute.
    /// </summary>
    private void Prune(long now)
    {
        if (_buckets.Count < 256 || now - _lastPruneMs < 60_000) return;
        _lastPruneMs = now;

        foreach (var kv in _buckets)
        {
            var b = kv.Value;
            lock (b)
            {
                if (b.Tokens + (now - b.LastRefillMs) * _refillPerMs >= _capacity)
                    _buckets.TryRemove(kv.Key, out _);
            }
        }
    }
}

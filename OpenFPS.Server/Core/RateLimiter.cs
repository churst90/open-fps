using System.Collections.Concurrent;
using System.Net;

namespace OpenFPS.Server.Core;

/// <summary>
/// A token bucket per key, used to put a ceiling on how often one source may attempt an expensive or
/// security-sensitive operation.
///
/// Login and registration are both: each verifies a bcrypt hash (deliberately slow) and each is a
/// credential guess. Unthrottled, one host can both mine the account list and occupy the server doing
/// it. The bucket is keyed by remote address rather than by connection id — a connection id is free to
/// churn, an address is not. See <see cref="AddressKey"/> for what "an address" means for IPv6.
/// </summary>
public sealed class RateLimiter
{
    private sealed class Bucket
    {
        public double Tokens;
        public long LastRefillMs;
    }

    /// <summary>
    /// The most keys held at once. Past this a prune runs at once rather than once a minute, and if
    /// every bucket is still in use a NEW key is refused until some refill. Refusing is the safe way
    /// round: a spray from a million addresses must not be able to grow the table without bound, and
    /// the keys already in it are the ones that were here first.
    /// </summary>
    public const int MaxKeys = 10_000;

    private const int PruneAbove = 256;
    private const long PruneEveryMs = 60_000;

    private readonly int _capacity;
    private readonly double _refillPerMs;
    private readonly Func<long> _nowMs;
    private readonly ConcurrentDictionary<string, Bucket> _buckets = new();
    private long _lastPruneMs;

    /// <param name="capacity">Attempts allowed in a burst.</param>
    /// <param name="refillPerSecond">Sustained attempts per second once the burst is spent.</param>
    /// <param name="nowMs">A millisecond clock; the default is <see cref="Environment.TickCount64"/>.
    /// Tests pass their own so the refill rate can be pinned without sleeping.</param>
    public RateLimiter(int capacity, double refillPerSecond, Func<long>? nowMs = null)
    {
        _capacity = Math.Max(1, capacity);
        _refillPerMs = refillPerSecond / 1000.0;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _lastPruneMs = _nowMs();
    }

    /// <summary>How many keys are being tracked now.</summary>
    public int Count => _buckets.Count;

    /// <summary>Takes one token for <paramref name="key"/>. False means the caller is over its limit.</summary>
    public bool TryConsume(string key) => TryConsume(key, 1.0);

    /// <summary>
    /// Takes <paramref name="cost"/> tokens for <paramref name="key"/>: an attempt that costs more than one
    /// (a row of fifty things is more work than one nudge). False, and nothing taken, if there are not that
    /// many; a cost above the burst is charged as the whole burst, so it is never refused for ever.
    /// </summary>
    public bool TryConsume(string key, double cost)
    {
        cost = Math.Clamp(cost, 0.0, _capacity);
        long now = _nowMs();
        Prune(now);

        if (!_buckets.TryGetValue(key, out var bucket))
        {
            if (_buckets.Count >= MaxKeys)
            {
                Prune(now, force: true);
                if (_buckets.Count >= MaxKeys) return false;
            }
            bucket = _buckets.GetOrAdd(key, _ => new Bucket { Tokens = _capacity, LastRefillMs = now });
        }

        lock (bucket)
        {
            bucket.Tokens = Refilled(bucket, now);
            bucket.LastRefillMs = now;

            if (bucket.Tokens < cost) return false;
            bucket.Tokens -= cost;
        }
        return true;
    }

    /// <summary>Gives a key its whole burst back, as if it had never been used.</summary>
    public void Reset(string key) => _buckets.TryRemove(key, out _);

    /// <summary>
    /// The keys that would be refused right now, with the seconds until they may try again. For the
    /// admin's /throttled.
    /// </summary>
    public List<(string Key, double SecondsUntilNext)> Throttled()
    {
        long now = _nowMs();
        var list = new List<(string, double)>();
        foreach (var kv in _buckets)
        {
            double tokens;
            lock (kv.Value) tokens = Refilled(kv.Value, now);
            if (tokens >= 1.0) continue;
            double wait = _refillPerMs > 0 ? (1.0 - tokens) / _refillPerMs / 1000.0 : double.PositiveInfinity;
            list.Add((kv.Key, wait));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    private double Refilled(Bucket bucket, long now)
        => Math.Min(_capacity, bucket.Tokens + Math.Max(0, now - bucket.LastRefillMs) * _refillPerMs);

    /// <summary>
    /// Drops buckets that have refilled to full, so a spray of one-shot attempts from many addresses
    /// cannot grow the dictionary without bound. Runs at most once a minute, or at once when the table
    /// is at <see cref="MaxKeys"/>.
    /// </summary>
    private void Prune(long now, bool force = false)
    {
        if (!force && (_buckets.Count < PruneAbove || now - _lastPruneMs < PruneEveryMs)) return;
        _lastPruneMs = now;

        foreach (var kv in _buckets)
        {
            var b = kv.Value;
            lock (b)
            {
                if (Refilled(b, now) >= _capacity)
                    _buckets.TryRemove(kv.Key, out _);
            }
        }
    }

    /// <summary>
    /// The key one source is counted under: an IPv4 address as itself, an IPv6 address by its /64.
    ///
    /// One IPv6 host is normally given a whole /64 — eighteen quintillion addresses — so keying on the
    /// full address would hand anyone on IPv6 a fresh bucket per attempt. An IPv4 address mapped into
    /// IPv6 (::ffff:a.b.c.d, which a dual-stack socket reports) is the IPv4 address.
    /// </summary>
    public static string AddressKey(IPAddress? address)
    {
        if (address == null) return "unknown";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();

        byte[] bytes = address.GetAddressBytes();
        for (int i = 8; i < 16; i++) bytes[i] = 0;
        return new IPAddress(bytes).ToString() + "/64";
    }

    /// <summary><see cref="AddressKey(IPAddress?)"/> from text; text that is not an address is its own key.</summary>
    public static string AddressKey(string? address)
        => IPAddress.TryParse(address, out var parsed) ? AddressKey(parsed) : (address ?? "unknown");
}

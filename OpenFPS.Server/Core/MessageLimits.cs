using System;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// How often one account may do each thing a client can ask for (docs/SERVER_SECURITY.md, "Limits").
/// Counted per account once logged in, so reconnecting does not refill them, and per connection before.
/// Each limit is a burst and then a steady rate, set well above what anybody playing does; what is over it
/// is refused (said, for what a person typed) or dropped (for what a client sends by itself), and logged
/// once a minute per account, never once per message.
/// </summary>
public sealed class MessageLimits
{
    /// <summary>Lines of chat, /all, /pm and /t: six at once, then one every two seconds.</summary>
    public RateLimiter Chat { get; }
    /// <summary>Typed commands and editor menu choices: twenty at once, then five a second.</summary>
    public RateLimiter Commands { get; }
    /// <summary>World editor changes, by what each costs (WorldEditor.EditCost): twenty at once, then four a second.</summary>
    public RateLimiter Edits { get; }
    /// <summary>Going to another map (/join): three at once, then one every ten seconds. Each sends the whole map.</summary>
    public RateLimiter Travel { get; }
    /// <summary>Voice packets: two seconds' worth at once, then sixty a second (a talker sends fifty).</summary>
    public RateLimiter Voice { get; }
    /// <summary>Lists (players, friends, maps, inventory), the interact key and scoped shots: ten at once, then four a second.</summary>
    public RateLimiter Requests { get; }

    /// <summary>What a refusal is logged under, once a minute per account and kind.</summary>
    public AbuseLog Abuse { get; }

    public MessageLimits(Func<long>? clockMs = null)
    {
        Chat = new RateLimiter(capacity: 6, refillPerSecond: 0.5, clockMs);
        Commands = new RateLimiter(capacity: 20, refillPerSecond: 5, clockMs);
        Edits = new RateLimiter(capacity: 20, refillPerSecond: 4, clockMs);
        Travel = new RateLimiter(capacity: 3, refillPerSecond: 0.1, clockMs);
        Voice = new RateLimiter(capacity: 100, refillPerSecond: 60, clockMs);
        Requests = new RateLimiter(capacity: 10, refillPerSecond: 4, clockMs);
        Abuse = new AbuseLog(clockMs);
    }

    public const string ChatTooFast = "You are chatting too fast. Wait a moment.";
    public const string CommandsTooFast = "You are sending commands too fast. Wait a moment.";
    public const string EditsTooFast = "Too many edits at once. Wait a moment, then carry on.";
    public const string TravelTooFast = "You have travelled a lot just now. Wait a few seconds.";
    public const string CommandTooLong = "That command is too long.";
}

/// <summary>
/// Abuse said in the log once per interval per source and kind, with how many times it happened since it
/// was last said: a flood of ten thousand packets is one line a minute, not ten thousand lines.
/// </summary>
public sealed class AbuseLog
{
    private readonly RateLimiter _lines;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _since = new();

    /// <summary>Lines written, for the tests.</summary>
    public int Written;

    public AbuseLog(Func<long>? clockMs = null, double perSeconds = 60)
    {
        _lines = new RateLimiter(capacity: 1, refillPerSecond: 1.0 / perSeconds, clockMs);
    }

    /// <summary>Notes one refusal; writes a line if this source and kind has had none for the interval.</summary>
    public void Note(string source, string kind, string what)
    {
        string key = kind + "|" + source;
        if (_since.Count > RateLimiter.MaxKeys) _since.Clear();
        int n = _since.AddOrUpdate(key, 1, (_, c) => c + 1);
        if (!_lines.TryConsume(key)) return;
        _since.TryRemove(key, out _);
        System.Threading.Interlocked.Increment(ref Written);
        Log.Warning("Limit: {Source} {What} ({Count} time(s) since the last line about it).", AuthService.ForLog(source), what, n);
    }
}

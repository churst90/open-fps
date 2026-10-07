using System.Collections.Concurrent;
using System.Text;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>What a login or registration came to, and the account when it worked.</summary>
public readonly record struct AuthOutcome(bool Success, string Message, UserData? User = null);

/// <summary>
/// Every decision about letting somebody in: the limits, the rules for a new account, and the lockout.
///
/// Transport-free and synchronous, so the same rules hold for the UDP client and the MUD gateway and
/// can be tested without a socket. The server runs <see cref="Check"/> off the tick thread, because a
/// bcrypt verification takes a few hundred milliseconds and the tick is 33.
///
/// The limits, all per source address (an IPv6 address counts by its /64):
///  - login and registration together: a burst of 6, then one every 5 seconds;
///  - new accounts: a burst of 3, then one every 20 minutes.
/// And per name, whether or not the account exists: 10 wrong passwords in a row lock the name for
/// 15 minutes. The address that last logged in to the account successfully is not locked out, so
/// somebody guessing at a name cannot keep its owner out with it.
///
/// A refused login says the same thing whether the name exists or not, and takes as long.
/// Registration cannot avoid saying a name is taken; its own limit is what keeps that from being a
/// way to list the accounts.
/// </summary>
public sealed class AuthService
{
    public const int MinUsernameLength = 3;
    public const int MaxUsernameLength = 20;
    public const int MinPasswordLength = 8;
    /// <summary>bcrypt reads the first 72 bytes and ignores the rest; a longer password would only
    /// look stronger than it is.</summary>
    public const int MaxPasswordBytes = 72;
    /// <summary>Longer than anything typed into a login: not a name or a password, so not looked up.</summary>
    public const int MaxLoginFieldLength = 128;
    public const int LockoutAfterFailures = 10;
    public static readonly TimeSpan LockoutFor = TimeSpan.FromMinutes(15);
    /// <summary>A name with failures but no lock is forgotten after this long without another.</summary>
    public static readonly TimeSpan ForgetFailuresAfter = TimeSpan.FromHours(1);
    public const int MaxTrackedNames = 10_000;

    public const string InvalidCredentials = "Invalid Credentials";
    public const string TooManyAttempts = "Too many attempts. Wait a few seconds and try again.";

    /// <summary>
    /// Names nobody may register. "Server" is the sender of announcements and the message of the
    /// day; a player called that could speak as the server. The rest are the words the server uses
    /// for a role, or that a player would read as the server or as everybody.
    /// </summary>
    public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "server", "system", "admin", "administrator", "moderator", "mod", "staff", "dev", "developer",
        "owner", "openfps", "everyone", "everybody", "all", "you", "me", "nobody", "someone", "player",
    };

    private sealed class Strikes
    {
        public int Count;
        public DateTime LastUtc;
        public DateTime LockedUntilUtc;
    }

    private readonly IUserRepository _users;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<string, Strikes> _strikes = new(StringComparer.Ordinal);

    /// <summary>Login and registration attempts per address.</summary>
    public RateLimiter Attempts { get; }
    /// <summary>New accounts per address.</summary>
    public RateLimiter NewAccounts { get; }

    public AuthService(IUserRepository users, Func<DateTime>? utcNow = null, Func<long>? clockMs = null)
    {
        _users = users;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        Attempts = new RateLimiter(capacity: 6, refillPerSecond: 0.2, clockMs);
        NewAccounts = new RateLimiter(capacity: 3, refillPerSecond: 1.0 / (20 * 60), clockMs);
    }

    // ── Login ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes one attempt from the address's allowance. Synchronous and cheap, so the server can
    /// refuse a flood before it queues any hashing.
    /// </summary>
    public bool Admit(string address)
    {
        if (Attempts.TryConsume(RateLimiter.AddressKey(address))) return true;
        Log.Warning("Auth: attempt refused for {Address}: over the rate limit.", ForLog(address));
        return false;
    }

    /// <summary>Admit and Check together, for a caller with no tick thread to protect.</summary>
    public AuthOutcome Login(string address, string username, string password)
        => Admit(address) ? Check(address, username, password) : new AuthOutcome(false, TooManyAttempts);

    /// <summary>
    /// Checks a name and password that have already been admitted. Slow (bcrypt) and thread-safe.
    /// </summary>
    public AuthOutcome Check(string address, string username, string password)
    {
        username ??= "";
        password ??= "";
        if (username.Trim().Length == 0 || username.Length > MaxLoginFieldLength || password.Length == 0
            || password.Length > MaxLoginFieldLength || HasControl(username))
        {
            Log.Warning("Auth: login REJECTED for '{User}' from {Address}: not a well-formed name and password.",
                        ForLog(username), ForLog(address));
            return new AuthOutcome(false, InvalidCredentials);
        }

        string name = Fold(username);
        var now = _utcNow();
        var user = _users.GetUser(name);

        if (IsLocked(name, now, out var until)
            && !(user?.LastLoginAddress is { } home && home == address))
        {
            int minutes = Math.Max(1, (int)Math.Ceiling((until - now).TotalMinutes));
            Log.Warning("Auth: login REFUSED for '{User}' from {Address}: the name is locked for {Minutes} more minute(s).",
                        ForLog(name), ForLog(address), minutes);
            return new AuthOutcome(false, $"Too many failed logins for that name. Try again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
        }

        if (!_users.VerifyPassword(name, password) || user == null)
        {
            Strike(name, now);
            if (user != null) _users.RecordFailedLogin(name, address, now);
            Log.Warning("Auth: login REJECTED for '{User}' from {Address}: invalid credentials.", ForLog(name), ForLog(address));
            return new AuthOutcome(false, InvalidCredentials);
        }

        _strikes.TryRemove(name, out _);
        _users.RecordLogin(name, address, now);
        return new AuthOutcome(true, "Authenticated", _users.GetUser(name) ?? user);
    }

    // ── Registration ────────────────────────────────────────────────────────────────────────────

    public AuthOutcome Register(string address, string username, string password)
        => Admit(address) ? CheckRegistration(address, username, password) : new AuthOutcome(false, TooManyAttempts);

    /// <summary>Creates a player account if the name and password are acceptable and the address may.</summary>
    public AuthOutcome CheckRegistration(string address, string username, string password)
    {
        string? problem = ProblemWithNewUsername(username) ?? ProblemWithNewPassword(password, username);
        if (problem != null) return new AuthOutcome(false, problem);

        if (!NewAccounts.TryConsume(RateLimiter.AddressKey(address)))
        {
            Log.Warning("Auth: registration of '{User}' refused for {Address}: too many new accounts.", ForLog(username), ForLog(address));
            return new AuthOutcome(false, "Too many new accounts from your address. Try again later.");
        }

        if (!_users.AddUser(username.Trim(), password, UserRole.Player))
            return new AuthOutcome(false, "That username is already taken.");
        Log.Information("Auth: account '{User}' created from {Address}.", Fold(username), ForLog(address));
        return new AuthOutcome(true, "Registration Successful.");
    }

    /// <summary>Why a name cannot be registered, or null if it can.</summary>
    public static string? ProblemWithNewUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "A username is required.";
        string name = username.Trim();
        if (name.Length < MinUsernameLength || name.Length > MaxUsernameLength)
            return $"A username must be {MinUsernameLength} to {MaxUsernameLength} characters.";
        foreach (char c in name)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
                return "A username may use only letters, digits, hyphens and underscores.";
        if (!char.IsAsciiLetterOrDigit(name[0])) return "A username must start with a letter or a digit.";
        if (ReservedNames.Contains(name)) return "That username is reserved.";
        return null;
    }

    /// <summary>Why a password cannot be used for a new account, or null if it can.</summary>
    public static string? ProblemWithNewPassword(string? password, string? username = null)
    {
        if (string.IsNullOrEmpty(password)) return "A password is required.";
        if (password.Length < MinPasswordLength) return $"A password must be at least {MinPasswordLength} characters.";
        if (Encoding.UTF8.GetByteCount(password) > MaxPasswordBytes)
            return $"A password can be at most {MaxPasswordBytes} bytes, which is {MaxPasswordBytes} plain letters and digits.";
        if (HasControl(password)) return "A password cannot contain control characters.";
        if (username != null && password.Trim().Equals(username.Trim(), StringComparison.OrdinalIgnoreCase))
            return "A password cannot be the same as the username.";
        return null;
    }

    // ── Lockout ─────────────────────────────────────────────────────────────────────────────────

    private bool IsLocked(string name, DateTime now, out DateTime until)
    {
        until = default;
        if (!_strikes.TryGetValue(name, out var s)) return false;
        lock (s)
        {
            until = s.LockedUntilUtc;
            return s.LockedUntilUtc > now;
        }
    }

    private void Strike(string name, DateTime now)
    {
        if (!_strikes.ContainsKey(name) && _strikes.Count >= MaxTrackedNames) MakeRoom(now);
        var s = _strikes.GetOrAdd(name, _ => new Strikes());
        lock (s)
        {
            // A lock that has run out starts the count again.
            if (s.LockedUntilUtc != default && s.LockedUntilUtc <= now) { s.Count = 0; s.LockedUntilUtc = default; }
            s.Count++;
            s.LastUtc = now;
            if (s.Count >= LockoutAfterFailures)
            {
                s.LockedUntilUtc = now + LockoutFor;
                Log.Warning("Auth: '{User}' locked for {Minutes} minutes after {Count} failed logins.",
                            ForLog(name), LockoutFor.TotalMinutes, s.Count);
            }
        }
    }

    /// <summary>
    /// Keeps the failure table bounded: forgets names that are not locked and have not failed for an
    /// hour, and if that is not enough, the unlocked name that failed longest ago. A name being
    /// guessed at right now is the newest entry, so it is the last to go.
    /// </summary>
    private void MakeRoom(DateTime now)
    {
        foreach (var kv in _strikes)
            lock (kv.Value)
                if (kv.Value.LockedUntilUtc <= now && now - kv.Value.LastUtc > ForgetFailuresAfter)
                    _strikes.TryRemove(kv.Key, out _);

        if (_strikes.Count < MaxTrackedNames) return;
        var oldest = _strikes.Where(kv => kv.Value.LockedUntilUtc <= now)
                             .OrderBy(kv => kv.Value.LastUtc).Select(kv => kv.Key).FirstOrDefault();
        if (oldest != null) _strikes.TryRemove(oldest, out _);
    }

    /// <summary>Names locked now, with when each lock ends and how many failures led to it.</summary>
    public List<(string Name, int Failures, DateTime LockedUntilUtc)> LockedNames()
    {
        var now = _utcNow();
        var list = new List<(string, int, DateTime)>();
        foreach (var kv in _strikes)
            lock (kv.Value)
                if (kv.Value.LockedUntilUtc > now) list.Add((kv.Key, kv.Value.Count, kv.Value.LockedUntilUtc));
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    /// <summary>Failed logins in a row for a name, and when its lock ends if it is locked.</summary>
    public (int Failures, DateTime? LockedUntilUtc) StrikesFor(string username)
    {
        if (!_strikes.TryGetValue(Fold(username), out var s)) return (0, null);
        lock (s) return (s.Count, s.LockedUntilUtc > _utcNow() ? s.LockedUntilUtc : null);
    }

    /// <summary>Lifts a lock and forgets the failures. True if there was anything to forget.</summary>
    public bool Unlock(string username) => _strikes.TryRemove(Fold(username), out _);

    // ── Text ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The same folding the user store applies: trimmed, invariant lower case.</summary>
    public static string Fold(string username) => username.Trim().ToLowerInvariant();

    private static bool HasControl(string text) => text.Any(char.IsControl);

    /// <summary>
    /// Text from the network made safe for one log line: control characters (a newline would forge a
    /// second line) become '?', and it is cut at 64 characters.
    /// </summary>
    public static string ForLog(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, 65));
        foreach (char c in text)
        {
            if (sb.Length == 64) { sb.Append('…'); break; }
            sb.Append(char.IsControl(c) ? '?' : c);
        }
        return sb.ToString();
    }
}

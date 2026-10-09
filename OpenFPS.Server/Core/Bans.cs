using System.Globalization;
using System.Text.RegularExpressions;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Core;

/// <summary>
/// Account bans: how long one is typed, how it is said, and who may ban whom. The ban itself is kept on
/// the account (IUserRepository.SetBan) and refused at login (AuthService.Check).
/// </summary>
public static class Bans
{
    /// <summary>The longest ban that can be typed; longer is until lifted.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromDays(7 * 520);
    /// <summary>A reason is kept to this many characters.</summary>
    public const int MaxReasonChars = 200;

    private static readonly Regex Duration = new(@"^(\d{1,6})(m|min|mins|h|hr|hrs|d|w)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"30m", "2h", "7d", "4w". False for anything else, which is then the start of the reason.</summary>
    public static bool TryParseDuration(string typed, out TimeSpan span)
    {
        span = default;
        var m = Duration.Match(typed.Trim());
        if (!m.Success) return false;
        long n = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (n <= 0) return false;
        double minutes = char.ToLowerInvariant(m.Groups[2].Value[0]) switch
        {
            'm' => n, 'h' => n * 60.0, 'd' => n * 60.0 * 24, _ => n * 60.0 * 24 * 7,
        };
        span = TimeSpan.FromMinutes(Math.Min(minutes, Longest.TotalMinutes));
        return true;
    }

    /// <summary>"14 October, 18:00 UTC", with the year when it is not this one.</summary>
    public static string When(DateTime utc, DateTime now, bool comma = true)
    {
        string day = utc.Year == now.Year
            ? utc.ToString("d MMMM", CultureInfo.InvariantCulture)
            : utc.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);
        return $"{day}{(comma ? "," : "")} {utc.ToString("HH:mm", CultureInfo.InvariantCulture)} UTC";
    }

    private static string Why(string? reason) => string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason.Trim();

    /// <summary>What a banned player is told, at login and when the ban throws them out.</summary>
    public static string Refusal(DateTime? untilUtc, string? reason, DateTime now)
        => untilUtc is { } until ? $"You are banned until {When(until, now)}: {Why(reason)}." : $"You are banned: {Why(reason)}.";

    /// <summary>One line of /bans.</summary>
    public static string Line(UserData u, DateTime now) => $"{u.Username}: {Detail(u, now)}";

    /// <summary>"by sean, 9 October 12:00 UTC, until lifted: spamming."</summary>
    public static string Detail(UserData u, DateTime now)
        => $"by {(string.IsNullOrEmpty(u.BannedBy) ? "somebody" : u.BannedBy)}, {When(u.BannedUtc ?? now, now, comma: false)}, "
         + $"{(u.BannedUntilUtc is { } until ? $"until {When(until, now, comma: false)}" : "until lifted")}: {Why(u.BanReason)}.";

    /// <summary>The built-in roles in order of power; a custom role is a Player's.</summary>
    public static int Rank(UserRole role) => role switch
    {
        UserRole.Owner => 4,
        UserRole.Admin => 3,
        UserRole.Moderator or UserRole.Dev => 2,
        _ => 1,
    };

    /// <summary>
    /// Why <paramref name="banner"/> may not ban this account, or null if they may. Nobody bans an owner or
    /// themselves; below an owner, only somebody whose role is above the target's, and a protected account
    /// only by somebody protected too, as with /kick. Among players, one trusted with ban may ban one who
    /// is not.
    /// </summary>
    public static string? Refusal(UserSession banner, string target, UserRole targetRole, Func<string, bool> targetCan)
    {
        if (target.Equals(banner.Username, StringComparison.OrdinalIgnoreCase)) return "You cannot ban yourself.";
        if (targetRole == UserRole.Owner) return $"{target} is an owner; nobody can ban an owner.";
        if (banner.Role == UserRole.Owner) return null;
        if (targetCan(Permissions.Protected) && !banner.Can(Permissions.Protected)) return $"You cannot ban {target}.";
        int mine = Rank(banner.Role), theirs = Rank(targetRole);
        if (theirs > mine || (theirs == mine && (theirs > 1 || targetCan("ban"))))
            return $"You cannot ban {target}: their role is not below yours.";
        return null;
    }
}

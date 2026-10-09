using OpenFPS.Common.Networking;
using OpenFPS.Server.Repositories;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>/ban, /unban and /bans: account bans, kept with the account and refused at login.</summary>
public partial class CommandHandler
{
    private const string BanUsage = "Usage: /ban NAME [DURATION] [REASON]. A duration is 30m, 2h, 7d or 4w; without one the ban lasts until /unban.";

    /// <summary>/ban NAME [DURATION] [REASON...]: bans the account and throws it out if it is on.</summary>
    private void HandleBan(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, BanUsage); return; }
        if (_users?.GetUser(args[0]) is not { } record) { Say(reply, $"There is no account called {args[0]}."); return; }
        // By the whole name: /kick's nearest-name match could ban somebody else who is on.
        var online = _sessions.GetAllSessions().FirstOrDefault(s => s.Username.Equals(record.Username, StringComparison.OrdinalIgnoreCase));
        if (Bans.Refusal(session, record.Username, record.Role, p => online?.Can(p) ?? AccountCan(record, p)) is { } refused)
        { Say(reply, refused); return; }

        TimeSpan? span = null;
        int from = 1;
        if (args.Length > 1 && Bans.TryParseDuration(args[1], out var typed)) { span = typed; from = 2; }
        string reason = string.Join(" ", args.Skip(from)).Trim();
        if (reason.Length > Bans.MaxReasonChars) reason = reason[..Bans.MaxReasonChars];

        var now = DateTime.UtcNow;
        DateTime? until = span is { } s ? now + s : null;
        bool replaces = record.IsBannedAt(now);
        if (!_users.SetBan(record.Username, now, until, session.Username, reason)) { Say(reply, "Bans cannot be kept on this server."); return; }
        Log.Information("Moderation: {By} banned {User} until {Until}. {Why}", session.Username, record.Username,
                        until is { } u ? Bans.When(u, now) : "lifted", reason);

        string told = Bans.Refusal(until, reason, now);
        if (online != null) _server.Kick(online, told);
        string how = until is { } end ? $"until {Bans.When(end, now)}" : "until lifted";
        Say(reply, $"Banned {record.Username} {how}: {(reason.Length > 0 ? reason : "no reason given")}."
                 + (replaces ? " It replaces the ban they had." : "")
                 + (online != null ? " They were online and have been removed." : ""));
    }

    /// <summary>/unban NAME.</summary>
    private void HandleUnban(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /unban NAME"); return; }
        if (_users?.GetUser(args[0]) is not { } record) { Say(reply, $"There is no account called {args[0]}."); return; }
        bool held = record.IsBannedAt(DateTime.UtcNow);
        // An ended ban is cleared as well, and was not one to lift.
        if (!_users.ClearBan(record.Username) || !held) { Say(reply, $"{record.Username} is not banned."); return; }
        Log.Information("Moderation: {By} lifted the ban on {User}.", session.Username, record.Username);
        Say(reply, $"{record.Username} is no longer banned.");
    }

    /// <summary>/bans: every ban that holds, one line each. Ended ones are lifted on the way.</summary>
    private void HandleBans(Action<IMessage> reply)
    {
        var now = DateTime.UtcNow;
        var recorded = _users?.Banned() ?? Array.Empty<UserData>();
        foreach (var ended in recorded.Where(u => !u.IsBannedAt(now))) _users!.ClearBan(ended.Username);
        var held = recorded.Where(u => u.IsBannedAt(now)).ToList();
        if (held.Count == 0) { Say(reply, "Nobody is banned."); return; }
        Say(reply, held.Count == 1 ? "1 ban:" : $"{held.Count} bans:");
        foreach (var u in held) Say(reply, Bans.Line(u, now));
    }

    /// <summary>What an account that is not online may do: its role, its grants and its custom role.</summary>
    private bool AccountCan(UserData record, string permission)
    {
        string custom = record.CustomRole is { } c && _server.Roles.Exists(c) ? c : "";
        return Permissions.Has(record.Role, Permissions.Parse(record.Permissions), permission)
            || _server.Roles.PermissionsOf(custom).Contains(permission);
    }
}

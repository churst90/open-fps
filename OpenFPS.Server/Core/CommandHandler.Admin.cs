using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using Arch.Core;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Administration: connections, accounts, roles and permissions. These show remote addresses, which are
/// personal data, and change other people's accounts.
/// </summary>
public partial class CommandHandler
{
    private static string When(DateTime? utc) => utc is { } t ? t.ToString("yyyy-MM-dd HH:mm") + " UTC" : "never";

    /// <summary>"3 h 5 min", "12 min", "40 s".</summary>
    public static string Span(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} d {span.Hours} h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes} min";
        if (span.TotalMinutes >= 1) return $"{(int)span.TotalMinutes} min";
        return $"{(int)span.TotalSeconds} s";
    }

    /// <summary>/sessions: everybody connected, and the connections that have not logged in.</summary>
    private void HandleSessions(Action<IMessage> reply)
    {
        var now = DateTime.UtcNow;
        var sessions = _sessions.GetAllSessions().OrderBy(s => s.Username, StringComparer.OrdinalIgnoreCase).ToList();
        Say(reply, sessions.Count == 1 ? "1 session:" : $"{sessions.Count} sessions:");
        foreach (var s in sessions)
        {
            int? ping = s.IsTextClient ? null : _server.PingOf(s.ConnectionId);
            Say(reply, $"  {s.Username}, {RoleWord(s.Role)}, {(s.IsTextClient ? "MUD" : "UDP")} from {Address(s.RemoteAddress)}, "
                     + $"on {s.CurrentMapId}{(s.Entity == Entity.Null ? ", not in the world yet" : "")}, "
                     + $"logged in {When(s.LoggedInUtc)} ({Span(now - s.LoggedInUtc)} ago), "
                     + $"{(s.Away ? "away" : $"idle {Span(now - s.LastActivityUtc)}")}"
                     + (ping is { } p ? $", ping {p} ms" : "") + $", connection {s.ConnectionId}.");
        }

        var pending = _server.PendingConnections();
        if (pending.Count > 0)
        {
            Say(reply, pending.Count == 1 ? "1 connection not logged in:" : $"{pending.Count} connections not logged in:");
            foreach (var c in pending)
                Say(reply, $"  {c.Transport} from {Address(c.Address)}, open {Span(now - c.SinceUtc)}, connection {c.Id}.");
        }
    }

    private static string Address(string address) => string.IsNullOrEmpty(address) ? "an unknown address" : address;

    /// <summary>/user NAME: an account's record: role, dates, addresses, failures, lock.</summary>
    private void HandleUser(string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /user [name]"); return; }
        var record = _users?.GetUser(args[0]);
        var (strikes, lockedUntil) = _server.Auth.StrikesFor(args[0]);
        string lockText = lockedUntil is { } until
            ? $" Locked for {Span(until - DateTime.UtcNow)} more after {strikes} failed logins in a row; /unlock {AuthService.Fold(args[0])} lifts it."
            : strikes > 0 ? $" {strikes} failed login{(strikes == 1 ? "" : "s")} in a row since the server started." : "";

        if (record == null)
        {
            Say(reply, $"There is no account called {args[0]}.{lockText}");
            return;
        }

        string created = record.CreatedUtc is { } c ? $"Created {When(c)}." : "Created before the server kept dates.";
        string login = record.LastLoginUtc is { } l
            ? $"Last login {When(l)} from {Address(record.LastLoginAddress ?? "")}."
            : "No login recorded.";
        string failed = record.FailedLogins > 0
            ? $" {record.FailedLogins} wrong password{(record.FailedLogins == 1 ? "" : "s")} since, the last {When(record.LastFailedUtc)} from {Address(record.LastFailedAddress ?? "")}."
            : "";
        var online = OnlineSession(record.Username);
        string now = online != null
            ? $" Online now, {(online.IsTextClient ? "MUD" : "UDP")} from {Address(online.RemoteAddress)}, on {online.CurrentMapId}."
            : " Not online.";
        string realName = string.IsNullOrEmpty(record.RealName) ? "" : $" Real name {record.RealName}.";
        string ban = record.IsBannedAt(DateTime.UtcNow) ? " Banned " + Bans.Detail(record, DateTime.UtcNow) : "";
        Say(reply, $"{record.Username}, {RoleWord(record.Role)}. {created} {login}{failed}{lockText}{now}{realName}{ban}");
    }

    /// <summary>/throttled: addresses over a limit now, and names locked now.</summary>
    private void HandleThrottled(Action<IMessage> reply)
    {
        var auth = _server.Auth;
        var attempts = auth.Attempts.Throttled();
        var accounts = auth.NewAccounts.Throttled();
        var locked = auth.LockedNames();
        if (attempts.Count == 0 && accounts.Count == 0 && locked.Count == 0)
        {
            Say(reply, "Nothing is throttled and no name is locked.");
            return;
        }
        foreach (var (key, wait) in attempts)
            Say(reply, $"  {key}: over the login limit, next try in {Span(TimeSpan.FromSeconds(Math.Ceiling(wait)))}.");
        foreach (var (key, wait) in accounts)
            Say(reply, $"  {key}: over the new-account limit, next in {Span(TimeSpan.FromSeconds(Math.Ceiling(wait)))}.");
        var now = DateTime.UtcNow;
        foreach (var (name, failures, until) in locked)
            Say(reply, $"  {name}: locked for {Span(until - now)} more after {failures} failed logins.");
    }

    /// <summary>/unlock NAME or ADDRESS: lifts a name's lock, or gives an address its limits back.</summary>
    private void HandleUnlock(string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /unlock [name or address]"); return; }
        var auth = _server.Auth;
        if (System.Net.IPAddress.TryParse(args[0], out var ip))
        {
            string key = RateLimiter.AddressKey(ip);
            auth.Attempts.Reset(key);
            auth.NewAccounts.Reset(key);
            Log.Information("Admin: limits reset for {Address}.", key);
            Say(reply, $"{key} may log in and create accounts again.");
            return;
        }
        bool was = auth.Unlock(args[0]);
        if (was) Log.Information("Admin: login lock lifted for '{User}'.", AuthService.ForLog(AuthService.Fold(args[0])));
        Say(reply, was ? $"{AuthService.Fold(args[0])} is unlocked and its failures forgotten."
                       : $"{AuthService.Fold(args[0])} was not locked.");
    }

    /// <summary>
    /// /grant and /revoke NAME PERMISSION: one permission on top of the account's role, kept with the
    /// account: a command's main name (tp, not move) or a named one such as fire-any. /perms lists them.
    /// </summary>
    private void HandleGrant(UserSession session, string[] args, Action<IMessage> reply, bool give)
    {
        string verb = give ? "grant" : "revoke";
        if (args.Length < 2) { Say(reply, $"Usage: /{verb} NAME PERMISSION. /perms lists the permissions."); return; }
        string perm = Permissions.Canonical(args[1].TrimStart('/').ToLowerInvariant());
        if (!Permissions.IsGated(perm)) { Say(reply, $"'{args[1]}' is not a permission. /perms lists them."); return; }
        if (!Permissions.Grantable(perm)) { Say(reply, "Roles and permissions stay with administrators: make them an administrator instead."); return; }
        if (_users == null || _users.GetUser(args[0]) is not { } record) { Say(reply, $"There is no account called {args[0]}."); return; }
        if (record.Role == UserRole.Owner) { Say(reply, $"{record.Username} is an owner and has every permission; there is nothing to {verb}."); return; }
        // Without grant-any: only to a player, and only what the granter can do.
        if (!session.Can(Permissions.GrantAny) && GrantCeiling(session, record, perm) is { } refused) { Say(reply, refused); return; }
        var grants = Permissions.Parse(record.Permissions);
        bool changed = give ? grants.Add(perm) : grants.Remove(perm);
        if (!changed)
        {
            Say(reply, give ? $"{record.Username} already has {perm}." : $"{record.Username} was not granted {perm}.");
            return;
        }
        if (!_users.SetGrants(record.Username, Permissions.Format(grants))) { Say(reply, "Permissions cannot be changed on this server."); return; }
        Log.Information("Admin: {Admin} {Verb} {Perm} for {User}.", session.Username, give ? "granted" : "revoked", perm, record.Username);
        if (OnlineSession(record.Username) is { } online)
        {
            online.Grants = new HashSet<string>(grants);
            _server.SendToSession(online, new TextEvent { Text = give
                ? $"{session.Username} gave you {perm}: {Permissions.Describe(perm)}."
                : $"{session.Username} took {perm} from you." });
        }
        string note = give && Permissions.RoleHas(record.Role, perm) ? $" Their role already allows it." : "";
        Say(reply, (give ? $"Granted {perm} to {record.Username}." : $"Revoked {perm} from {record.Username}.") + note);
    }

    /// <summary>/perms: your own role and what it and your grants allow. /perms NAME (admins): theirs.
    /// /perms all: every permission and what it is.</summary>
    private void HandlePerms(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length > 0 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            Say(reply, "Permissions: " + string.Join("; ", Permissions.All.Select(p => $"{p}, {Permissions.Describe(p)}")) + ".");
            return;
        }
        UserRole role = session.Role;
        HashSet<string> grants = session.Grants;
        string custom = session.CustomRole;
        string who = "You are";
        if (args.Length > 0 && !args[0].Equals(session.Username, StringComparison.OrdinalIgnoreCase))
        {
            if (!session.Can(Permissions.PermsAny)) { DenyCommand(reply); return; }
            if (_users == null || _users.GetUser(args[0]) is not { } record) { Say(reply, $"There is no account called {args[0]}."); return; }
            role = record.Role; grants = Permissions.Parse(record.Permissions); who = $"{record.Username} is";
            custom = record.CustomRole is { } c && _server.Roles.Exists(c) ? c : "";
        }
        var byRole = Permissions.All.Where(p => Permissions.RoleHas(role, p)).Concat(_server.Roles.PermissionsOf(custom)).Distinct().ToList();
        string roleText = RoleText(role, byRole);
        string granted = grants.Count == 0 ? "No single permissions granted." : $"Granted: {string.Join(", ", grants.OrderBy(g => g))}.";
        string word = custom.Length > 0 ? custom : RoleWord(role);
        string article = "aeiou".Contains(char.ToLowerInvariant(word[0])) ? "an" : "a";
        Say(reply, $"{who} {article} {word}: {roleText}. {granted}");
    }

    /// <summary>/setrole NAME ROLE: a built-in or custom role, and the session's too if they are on.</summary>
    private void HandleSetRole(UserSession session, string[] args, Action<IMessage> reply)
    {
        var roles = _server.Roles;
        string customList = roles.Names.Count > 0 ? ", or one you made: " + string.Join(", ", roles.Names) : "";
        if (args.Length < 2) { Say(reply, $"Usage: /setrole NAME ROLE. Roles: player, moderator, dev, admin, owner{customList}."); return; }
        UserRole? role = Permissions.BuiltInRole(args[1]);
        string custom = role == null && roles.Exists(args[1]) ? RoleRepository.Key(args[1]) : "";
        if (role == null && custom.Length == 0)
        { Say(reply, $"'{args[1]}' is not a role. Roles: player, moderator, dev, admin, owner{customList}."); return; }
        if (_users == null || _users.GetUser(args[0]) is not { } record) { Say(reply, $"There is no account called {args[0]}."); return; }
        // Not your own: an admin who demoted themselves by a slip would have nobody to put it back.
        if (record.Username.Equals(session.Username, StringComparison.OrdinalIgnoreCase))
        { Say(reply, "You cannot change your own role."); return; }
        // A custom role sits on top of Player.
        var newRole = role ?? UserRole.Player;
        if (OwnerRefusal(session, record, newRole) is { } refusal) { Say(reply, refusal); return; }
        // Touch the custom role only to set or clear one, so a store without custom roles still works.
        bool customChanges = custom.Length > 0 || !string.IsNullOrEmpty(record.CustomRole);
        if (!_users.SetRole(record.Username, newRole)
            || (customChanges && !_users.SetCustomRole(record.Username, custom.Length > 0 ? custom : null)))
        { Say(reply, "Roles cannot be changed on this server."); return; }

        Log.Information("Admin: {Admin} set {User}'s role to {Role}.", session.Username, record.Username, custom.Length > 0 ? custom : newRole.ToString());
        string word = custom.Length > 0 ? custom : RoleWord(newRole);
        string article = "aeiou".Contains(char.ToLowerInvariant(word[0])) ? "an" : "a";
        var online = OnlineSession(record.Username);
        if (online != null)
        {
            online.Role = newRole;
            online.CustomRole = custom;
            online.RolePermissions = roles.PermissionsOf(custom);
            if (_maps.TryGetMap(online.CurrentMapId, out var world, out _, out _, out _)
                && online.Entity != Entity.Null && world.IsAlive(online.Entity) && world.Has<PlayerComponent>(online.Entity))
            {
                ref var player = ref world.Get<PlayerComponent>(online.Entity);
                player.Role = newRole;
            }
            _server.SendToSession(online, new TextEvent { Text = $"{session.Username} made you {article} {word}." + RoleSummary(online) });
        }
        Say(reply, $"{record.Username} is now {article} {word}.");
    }

    /// <summary>
    /// Why this role change would touch an owner and may not, or null. Only an owner makes or unmakes
    /// owners, and the last one keeps it, so the server always has somebody who can do everything.
    /// </summary>
    private string? OwnerRefusal(UserSession changer, UserData target, UserRole newRole)
    {
        bool isOwner = target.Role == UserRole.Owner;
        if (!isOwner && newRole != UserRole.Owner) return null;
        if (!changer.Can(Permissions.Owners))
            return isOwner ? $"{target.Username} is an owner; only an owner can change an owner's role."
                           : "Only an owner can make somebody an owner.";
        if (isOwner && newRole != UserRole.Owner
            && !_users!.UsernamesWithRole(UserRole.Owner).Any(n => !n.Equals(target.Username, StringComparison.OrdinalIgnoreCase)))
            return $"{target.Username} is the server's last owner. Make somebody else an owner first.";
        return null;
    }

    private static string RoleText(UserRole role, List<string> byRole) => role switch
    {
        UserRole.Owner => "every permission",
        UserRole.Admin => "every permission but owners",
        _ => byRole.Count == 0 ? "nothing beyond play" : string.Join(", ", byRole),
    };

    /// <summary>" You can now: tp, where." for a role that is a list of commands; nothing for player.</summary>
    private static string RoleSummary(UserSession s)
    {
        if (s.Role is UserRole.Owner or UserRole.Admin) return " You can use every command.";
        var can = Permissions.All.Where(s.Can).ToList();
        return can.Count == 0 ? "" : $" You can now use: {string.Join(", ", can)}.";
    }

    /// <summary>
    /// /role list, create NAME [PERMISSION ...], add or remove NAME PERMISSION, show NAME, delete NAME.
    /// A custom role is Player plus its permissions; /setrole gives it to somebody.
    /// </summary>
    private void HandleRole(UserSession session, string[] args, Action<IMessage> reply)
    {
        var roles = _server.Roles;
        string sub = args.Length > 0 ? args[0].ToLowerInvariant() : "list";
        if (sub == "list")
        {
            Say(reply, roles.Names.Count == 0
                ? "No custom roles. /role create NAME, then /role add NAME PERMISSION."
                : "Custom roles: " + string.Join("; ", roles.Names.Select(n => $"{n}, {Describe(roles.PermissionsOf(n))}")) + ".");
            return;
        }
        if (args.Length < 2) { Say(reply, "Usage: /role list, create NAME [PERMISSIONS], add NAME PERMISSION, remove NAME PERMISSION, show NAME, delete NAME."); return; }
        string name = RoleRepository.Key(args[1]);
        // The built-in roles are fixed: nothing here renames, changes or removes one, Owner least of all.
        if (Permissions.BuiltInRole(name) is { } builtIn)
        {
            var has = Permissions.All.Where(p => Permissions.RoleHas(builtIn, p)).ToList();
            Say(reply, sub == "show"
                ? $"{name}: {RoleText(builtIn, has)}. A built-in role; it cannot be changed."
                : builtIn == UserRole.Owner
                    ? "owner is a built-in role with every permission. It cannot be changed or removed."
                    : $"{name} is a built-in role. It cannot be changed or removed.");
            return;
        }
        switch (sub)
        {
            case "create":
            {
                if (!RoleRepository.IsValidName(name)) { Say(reply, "A role name is 2 to 20 letters, digits, '_' or '-', starting with a letter."); return; }
                if (!roles.Create(name)) { Say(reply, $"There is already a role called {name}."); return; }
                var refused = new List<string>();
                foreach (var p in args.Skip(2))
                {
                    string perm = Permissions.Canonical(p.TrimStart('/').ToLowerInvariant());
                    if (Grantable(perm)) roles.Change(name, perm, add: true); else refused.Add(p);
                }
                Log.Information("Admin: {Admin} created role {Role}: {Perms}.", session.Username, name, Describe(roles.PermissionsOf(name)));
                Say(reply, $"Made the role {name}: {Describe(roles.PermissionsOf(name))}."
                         + (refused.Count > 0 ? $" Not permissions, or not grantable: {string.Join(", ", refused)}." : ""));
                return;
            }
            case "add":
            case "remove":
            {
                if (!roles.Exists(name)) { Say(reply, $"There is no role called {name}."); return; }
                if (args.Length < 3) { Say(reply, $"Usage: /role {sub} NAME PERMISSION. /perms all lists them."); return; }
                string perm = Permissions.Canonical(args[2].TrimStart('/').ToLowerInvariant());
                if (!Grantable(perm)) { Say(reply, $"'{args[2]}' is not a permission a role can have. /perms all lists them."); return; }
                bool add = sub == "add";
                if (!roles.Change(name, perm, add)) { Say(reply, add ? $"{name} already has {perm}." : $"{name} does not have {perm}."); return; }
                foreach (var s in _sessions.GetAllSessions().Where(s => s.CustomRole == name))
                {
                    s.RolePermissions = roles.PermissionsOf(name);
                    _server.SendToSession(s, new TextEvent { Text = add
                        ? $"{session.Username} gave your role {perm}: {Permissions.Describe(perm)}."
                        : $"{session.Username} took {perm} from your role." });
                }
                Say(reply, $"{name}: {Describe(roles.PermissionsOf(name))}.");
                return;
            }
            case "show":
                Say(reply, roles.Exists(name) ? $"{name}: {Describe(roles.PermissionsOf(name))}." : $"There is no role called {name}.");
                return;
            case "delete":
            {
                if (!roles.Delete(name)) { Say(reply, $"There is no role called {name}."); return; }
                int cleared = _users?.ClearCustomRole(name) ?? 0;
                foreach (var s in _sessions.GetAllSessions().Where(s => s.CustomRole == name))
                {
                    s.CustomRole = ""; s.RolePermissions = new HashSet<string>();
                    _server.SendToSession(s, new TextEvent { Text = $"{session.Username} removed the role {name}; you are a player." });
                }
                Say(reply, $"Deleted the role {name}." + (cleared > 0 ? $" {cleared} account{(cleared == 1 ? " is" : "s are")} players again." : ""));
                return;
            }
            default:
                Say(reply, "Usage: /role list, create NAME [PERMISSIONS], add NAME PERMISSION, remove NAME PERMISSION, show NAME, delete NAME.");
                return;
        }
    }

    private static bool Grantable(string perm) => Permissions.Grantable(perm);

    private static string Describe(HashSet<string> perms) => perms.Count == 0 ? "no permissions yet" : string.Join(", ", perms.OrderBy(p => p));

    private static string Article(UserRole role) => "aeiou".Contains(RoleWord(role)[0]) ? "an" : "a";
}

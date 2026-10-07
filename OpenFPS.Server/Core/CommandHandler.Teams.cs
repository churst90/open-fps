using OpenFPS.Common.Networking;
using OpenFPS.Server.Repositories;

namespace OpenFPS.Server.Core;

public partial class CommandHandler
{
    // ── Teams ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// /team — yours. /team create NAME, /team invite NAME, /team join NAME, /team leave, /team list,
    /// /team kick NAME (the leader's), /team open and /team close (the leader's: whether anybody may
    /// join uninvited), /team chat MESSAGE (or /t MESSAGE).
    ///
    /// Not gated: every player may make a team, and a team is the people in it. Whenever somebody comes
    /// or goes, their body is given its new team and re-sent (<see cref="GameServer.RefreshTeam"/>), so
    /// everybody near them hears their beacon change tone without anybody leaving and coming back.
    /// </summary>
    private void HandleTeam(UserSession session, string[] args, Action<IMessage> reply)
    {
        var teams = _server.Teams;
        if (teams == null) { Say(reply, "Teams are not available on this server."); return; }

        string verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        string? arg = args.Length > 1 ? args[1] : null;
        var mine = teams.TeamOf(session.Username);

        // Everybody in a team who is on, except whoever is named: news of the team, to the team.
        void Tell(TeamRepository.Team team, string text, string? except = null)
        {
            foreach (var s in _sessions.GetAllSessions())
                if (team.Has(s.Username) && !s.Username.Equals(except, StringComparison.OrdinalIgnoreCase))
                    _server.SendToSession(s, new TextEvent { Text = text });
        }

        switch (verb)
        {
            case "":
            {
                if (mine == null)
                {
                    Say(reply, "You are not in a team. Say /team create and a name to start one, or /team join and a name.");
                    return;
                }
                int online = mine.Members.Count(m => OnlineSession(m) != null);
                string lead = mine.Leader.Equals(session.Username, StringComparison.OrdinalIgnoreCase) ? "you lead it" : $"led by {mine.Leader}";
                Say(reply, $"You are in team {mine.Name}, {lead}. {Count(mine.Members.Count, "member")}, {online} online."
                         + (mine.Open ? " It is open to anybody." : " It is invitation only."));
                return;
            }

            case "create":
            case "new":
            {
                if (string.IsNullOrWhiteSpace(arg)) { Say(reply, "Usage: /team create [name]"); return; }
                if (teams.Create(arg, session.Username) is { } problem) { Say(reply, problem); return; }
                _server.RefreshTeam(session.Username);
                Say(reply, $"You made team {arg.Trim()}, and you lead it. Say /team invite and a name to ask somebody in.");
                return;
            }

            case "invite":
            {
                if (mine == null) { Say(reply, "You are not in a team."); return; }
                if (string.IsNullOrWhiteSpace(arg)) { Say(reply, "Usage: /team invite [name]"); return; }
                if (!TryFindUser(arg, out var username, out _)) { Say(reply, $"There is no player called {arg}."); return; }
                var target = OnlineSession(username);
                if (target != null) username = target.Username;
                if (mine.Has(username)) { Say(reply, $"{username} is already in team {mine.Name}."); return; }
                if (mine.Members.Count >= TeamRepository.MaxMembers) { Say(reply, $"Team {mine.Name} is full, with {TeamRepository.MaxMembers} members."); return; }
                if (!teams.Invite(mine.Name, username)) { Say(reply, $"{username} is already invited."); return; }
                if (target != null)
                    _server.SendToSession(target, new TextEvent { Text = $"{session.Username} invites you to team {mine.Name}. Say /team join {mine.Name} to join." });
                Say(reply, $"You invited {username} to team {mine.Name}." + (target == null ? " They are not on; they can join when they are." : ""));
                return;
            }

            case "join":
            {
                if (string.IsNullOrWhiteSpace(arg)) { Say(reply, "Usage: /team join [name]"); return; }
                if (teams.Join(arg, session.Username) is { } problem) { Say(reply, problem); return; }
                var joined = teams.TeamOf(session.Username)!;
                _server.RefreshTeam(session.Username);
                Tell(joined, $"{session.Username} joined team {joined.Name}.", except: session.Username);
                Say(reply, $"You joined team {joined.Name}. {Count(joined.Members.Count, "member")}. Say /t and a message to talk to them.");
                return;
            }

            case "leave":
            case "quit":
            {
                var left = teams.Leave(session.Username, out string? newLeader, out bool ended);
                if (left == null) { Say(reply, "You are not in a team."); return; }
                _server.RefreshTeam(session.Username);
                if (!ended)
                {
                    var now = teams.Get(left.Name)!;
                    Tell(now, $"{session.Username} left team {left.Name}." + (newLeader != null ? $" {newLeader} leads it now." : ""));
                }
                Say(reply, ended ? $"You left team {left.Name}, and it is gone: you were the last in it." : $"You left team {left.Name}.");
                return;
            }

            case "kick":
            case "remove":
            {
                if (mine == null) { Say(reply, "You are not in a team."); return; }
                if (!mine.Leader.Equals(session.Username, StringComparison.OrdinalIgnoreCase))
                {
                    Say(reply, $"Only {mine.Leader}, who leads team {mine.Name}, can remove somebody from it.");
                    return;
                }
                if (string.IsNullOrWhiteSpace(arg)) { Say(reply, "Usage: /team kick [name]"); return; }
                string? who = mine.Members.FirstOrDefault(m => m.Equals(arg.Trim(), StringComparison.OrdinalIgnoreCase));
                if (who == null) { Say(reply, $"{arg} is not in team {mine.Name}."); return; }
                if (who.Equals(session.Username, StringComparison.OrdinalIgnoreCase)) { Say(reply, "To leave your own team, say /team leave."); return; }
                teams.Leave(who, out _, out _);
                _server.RefreshTeam(who);
                if (OnlineSession(who) is { } gone) _server.SendToSession(gone, new TextEvent { Text = $"{session.Username} removed you from team {mine.Name}." });
                Tell(teams.Get(mine.Name)!, $"{who} was removed from team {mine.Name}.", except: session.Username);
                Say(reply, $"You removed {who} from team {mine.Name}.");
                return;
            }

            case "open":
            case "close":
            {
                if (mine == null) { Say(reply, "You are not in a team."); return; }
                if (!mine.Leader.Equals(session.Username, StringComparison.OrdinalIgnoreCase))
                {
                    Say(reply, $"Only {mine.Leader}, who leads team {mine.Name}, can change who may join it.");
                    return;
                }
                teams.SetOpen(mine.Name, verb == "open");
                Say(reply, verb == "open" ? $"Team {mine.Name} is open: anybody may join." : $"Team {mine.Name} is invitation only.");
                return;
            }

            case "list":
            case "members":
            case "who":
            {
                // A named team, or your own; with neither, the teams there are.
                var team = !string.IsNullOrWhiteSpace(arg) ? teams.Get(arg) : mine;
                if (!string.IsNullOrWhiteSpace(arg) && team == null) { Say(reply, $"There is no team called {arg}."); return; }
                if (team == null)
                {
                    var all = teams.All();
                    Say(reply, all.Length == 0
                        ? "There are no teams yet. Say /team create and a name to start one."
                        : "Teams: " + string.Join("; ", all.Select(t => $"{t.Name}, {Count(t.Members.Count, "member")}{(t.Open ? ", open" : "")}")) + ".");
                    return;
                }
                var people = team.Members.Select(m =>
                {
                    string role = m.Equals(team.Leader, StringComparison.OrdinalIgnoreCase) ? ", leader" : "";
                    string you = m.Equals(session.Username, StringComparison.OrdinalIgnoreCase) ? " (you)" : "";
                    return $"{m}{you}{role}, {(OnlineSession(m) != null ? "online" : "offline")}";
                });
                Say(reply, $"Team {team.Name}: {string.Join("; ", people)}.");
                return;
            }

            case "chat":
            case "say":
                TeamChat(session, args.Skip(1).ToArray(), reply);
                return;

            default:
                Say(reply, "Say /team to hear yours, or /team create, invite, join, leave, list, kick, open, close, or chat.");
                return;
        }
    }

    /// <summary>/t MESSAGE, or /team chat MESSAGE: to everybody in your team who is on, wherever they are.</summary>
    private void TeamChat(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (_server.Teams?.TeamOf(session.Username) == null) { Say(reply, "You are not in a team."); return; }
        if (args.Length == 0) { Say(reply, "Usage: /t [message] — says it to your team."); return; }
        _server.Chat(session, string.Join(" ", args), ChatChannel.Team);
    }

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
}

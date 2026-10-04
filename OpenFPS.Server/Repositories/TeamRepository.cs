using System.Text.Json;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// The teams players have made, kept in teams.json beside friends.json, openfps.db and motd.txt.
///
/// A JSON file and not a table for the reason <see cref="FriendRepository"/> gives: the user store is
/// built once with EnsureCreated and would never grow a new table on a server already running.
///
/// A team is a name, a leader, its members in the order they joined, and the names it has invited.
/// A player is in one team at most, so whose side somebody is on has one answer. A team is closed by
/// default — you join by invitation — and its leader can open it to anybody. The leader leaving hands
/// the team to whoever has been in it longest; the last member leaving ends it.
///
/// Thread-safe: commands run on the tick thread, but a spawn and a command can arrive from different
/// paths, and every write goes to disk under the same lock.
/// </summary>
public class TeamRepository
{
    /// <summary>The most players one team holds.</summary>
    public const int MaxMembers = 16;
    public const int MinNameLength = 2, MaxNameLength = 20;

    public sealed class Team
    {
        public string Name { get; set; } = "";
        public string Leader { get; set; } = "";
        public List<string> Members { get; set; } = new();
        public List<string> Invited { get; set; } = new();
        /// <summary>Anybody may join without being invited.</summary>
        public bool Open { get; set; }

        public bool Has(string username) => Members.Any(m => m.Equals(username, StringComparison.OrdinalIgnoreCase));
        public bool IsInvited(string username) => Invited.Any(m => m.Equals(username, StringComparison.OrdinalIgnoreCase));
    }

    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, Team> _teams = new(StringComparer.OrdinalIgnoreCase);

    public TeamRepository(string path)
    {
        _path = Path.GetFullPath(path);
        Load();
    }

    public string PathOnDisk => _path;

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<List<Team>>(File.ReadAllText(_path));
            if (loaded == null) return;
            _teams = new Dictionary<string, Team>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in loaded)
                if (!string.IsNullOrWhiteSpace(t.Name) && t.Members.Count > 0) _teams[t.Name] = t;
        }
        catch (Exception ex)
        {
            // As with friends: a damaged file must neither take the server down nor be overwritten.
            Log.Error(ex, "TeamRepository: could not read {Path}; starting with no teams and keeping the file as .bad.", _path);
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch { }
        }
    }

    private void Save()
    {
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_teams.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>Why a name cannot be a team's, or null if it can: the same characters a username may use.</summary>
    public static string? ProblemWithName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "A team needs a name.";
        name = name.Trim();
        if (name.Length < MinNameLength || name.Length > MaxNameLength)
            return $"A team name must be {MinNameLength} to {MaxNameLength} characters.";
        foreach (char c in name)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
                return "A team name may use only letters, digits, hyphens and underscores.";
        if (!char.IsAsciiLetterOrDigit(name[0])) return "A team name must start with a letter or a digit.";
        return null;
    }

    /// <summary>A copy of a team, so a caller reading it cannot change it behind the lock.</summary>
    private static Team Copy(Team t) => new()
    {
        Name = t.Name, Leader = t.Leader, Open = t.Open,
        Members = new List<string>(t.Members), Invited = new List<string>(t.Invited),
    };

    public Team? Get(string name)
    {
        lock (_lock) return _teams.TryGetValue(name.Trim(), out var t) ? Copy(t) : null;
    }

    /// <summary>The team a player is in, or null.</summary>
    public Team? TeamOf(string username)
    {
        lock (_lock)
        {
            var t = _teams.Values.FirstOrDefault(t => t.Has(username));
            return t == null ? null : Copy(t);
        }
    }

    /// <summary>The name of a player's team, or "" — what goes on their body for the clients to see.</summary>
    public string NameOf(string username) => TeamOf(username)?.Name ?? "";

    public Team[] All()
    {
        lock (_lock) return _teams.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Select(Copy).ToArray();
    }

    /// <summary>Starts a team with one member, its leader. Null if it worked, or why not.</summary>
    public string? Create(string name, string leader)
    {
        if (ProblemWithName(name) is { } problem) return problem;
        name = name.Trim();
        lock (_lock)
        {
            if (_teams.Values.FirstOrDefault(t => t.Has(leader)) is { } mine)
                return $"You are already in team {mine.Name}. Leave it first.";
            if (_teams.TryGetValue(name, out var taken)) return $"There is already a team called {taken.Name}.";
            _teams[name] = new Team { Name = name, Leader = leader, Members = { leader } };
            Save();
            return null;
        }
    }

    /// <summary>Puts a name on a team's invitation list. False if it was there already.</summary>
    public bool Invite(string team, string username)
    {
        lock (_lock)
        {
            if (!_teams.TryGetValue(team, out var t) || t.IsInvited(username)) return false;
            t.Invited.Add(username);
            Save();
            return true;
        }
    }

    /// <summary>Joins a team. Null if it worked, or why not.</summary>
    public string? Join(string team, string username)
    {
        lock (_lock)
        {
            if (!_teams.TryGetValue(team.Trim(), out var t)) return $"There is no team called {team}.";
            if (_teams.Values.FirstOrDefault(x => x.Has(username)) is { } mine)
                return mine == t ? $"You are already in team {t.Name}." : $"You are already in team {mine.Name}. Leave it first.";
            if (!t.Open && !t.IsInvited(username)) return $"Team {t.Name} has not invited you.";
            if (t.Members.Count >= MaxMembers) return $"Team {t.Name} is full, with {MaxMembers} members.";
            t.Invited.RemoveAll(i => i.Equals(username, StringComparison.OrdinalIgnoreCase));
            t.Members.Add(username);
            Save();
            return null;
        }
    }

    /// <summary>
    /// Takes a player out of whatever team they are in. Returns the team as it was before, or null if
    /// they were in none; <paramref name="newLeader"/> is who leads it now if the leader left, and
    /// <paramref name="ended"/> says the team is gone because nobody is left in it.
    /// </summary>
    public Team? Leave(string username, out string? newLeader, out bool ended)
    {
        newLeader = null; ended = false;
        lock (_lock)
        {
            var t = _teams.Values.FirstOrDefault(x => x.Has(username));
            if (t == null) return null;
            var before = Copy(t);
            t.Members.RemoveAll(m => m.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (t.Members.Count == 0) { _teams.Remove(t.Name); ended = true; }
            else if (t.Leader.Equals(username, StringComparison.OrdinalIgnoreCase)) t.Leader = newLeader = t.Members[0];
            Save();
            return before;
        }
    }

    /// <summary>Opens a team to anybody, or closes it to invitation only.</summary>
    public void SetOpen(string team, bool open)
    {
        lock (_lock)
        {
            if (!_teams.TryGetValue(team, out var t) || t.Open == open) return;
            t.Open = open;
            Save();
        }
    }
}

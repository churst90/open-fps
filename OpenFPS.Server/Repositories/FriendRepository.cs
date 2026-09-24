using System.Text.Json;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// Who each player has called a friend, kept in friends.json beside openfps.db and motd.txt.
///
/// A JSON file and not a table, on purpose. The user store is EF Core over SQLite created with
/// <c>EnsureCreated</c>, which builds a schema once and never again: a new table added to the model
/// would simply not exist in every openfps.db already out there, and the first friend added would
/// throw. A migration pipeline is a larger change than a list of names needs. Keyed by the folded
/// (lower-case) username, which is what the user store keys on too.
///
/// One-directional, like a contact list: adding somebody does not add you to theirs.
/// Thread-safe: commands run on the tick thread but the friend list is answered from the dispatcher.
/// </summary>
public class FriendRepository
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, List<string>> _friends = new(StringComparer.OrdinalIgnoreCase);

    public FriendRepository(string path)
    {
        _path = Path.GetFullPath(path);
        Load();
    }

    public string PathOnDisk => _path;

    private static string Key(string username) => username.Trim().ToLowerInvariant();

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_path));
            if (loaded == null) return;
            _friends = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in loaded) _friends[Key(kv.Key)] = kv.Value ?? new List<string>();
        }
        catch (Exception ex)
        {
            // A damaged file must not take the server down, and must not be silently overwritten
            // either: keep it aside so the names can be recovered by hand.
            Log.Error(ex, "FriendRepository: could not read {Path}; starting with no friends and keeping the file as .bad.", _path);
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch { }
        }
    }

    private void Save()
    {
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_friends, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>The friends of a user, in the order they were added.</summary>
    public string[] GetFriends(string username)
    {
        lock (_lock)
            return _friends.TryGetValue(Key(username), out var list) ? list.ToArray() : Array.Empty<string>();
    }

    public bool IsFriend(string username, string other)
    {
        lock (_lock)
            return _friends.TryGetValue(Key(username), out var list)
                && list.Any(f => f.Equals(other, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Adds a friend. False if they were one already.</summary>
    public bool Add(string username, string friend)
    {
        lock (_lock)
        {
            if (!_friends.TryGetValue(Key(username), out var list))
                _friends[Key(username)] = list = new List<string>();
            if (list.Any(f => f.Equals(friend, StringComparison.OrdinalIgnoreCase))) return false;
            list.Add(friend);
            Save();
            return true;
        }
    }

    /// <summary>Removes a friend. False if they were not one.</summary>
    public bool Remove(string username, string friend)
    {
        lock (_lock)
        {
            if (!_friends.TryGetValue(Key(username), out var list)) return false;
            if (list.RemoveAll(f => f.Equals(friend, StringComparison.OrdinalIgnoreCase)) == 0) return false;
            Save();
            return true;
        }
    }
}

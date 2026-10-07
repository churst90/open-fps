using System.Text.Json;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// Who each player has called a friend, in friends.json beside openfps.db. One way, like a contact
/// list: adding somebody does not add you to theirs.
///
/// A file and not a table because the user store is created with <c>EnsureCreated</c>, which never
/// adds a table to a database that already exists: the first friend added would throw. Keyed by the
/// folded username, as the user store is. Locked: commands run on the tick thread, the list is
/// answered from the dispatcher.
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
            // A damaged file must not take the server down, nor be overwritten: kept aside for recovery.
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

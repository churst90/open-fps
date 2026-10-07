using System.Text.Json;
using Serilog;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// Roles an administrator made (/role): a name and its permissions, on top of Player. Kept in
/// roles.json beside friends.json; without a path (a test) in memory.
/// </summary>
public class RoleRepository
{
    private readonly string? _path;
    private readonly object _lock = new();
    private Dictionary<string, SortedSet<string>> _roles = new(StringComparer.OrdinalIgnoreCase);

    public RoleRepository(string? path)
    {
        _path = path == null ? null : Path.GetFullPath(path);
        Load();
    }

    public static string Key(string name) => name.Trim().ToLowerInvariant();

    /// <summary>A role name: 2 to 20 letters, digits, '_' or '-', starting with a letter.</summary>
    public static bool IsValidName(string name)
        => name.Length is >= 2 and <= 20 && char.IsLetter(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-');

    public IReadOnlyList<string> Names { get { lock (_lock) return _roles.Keys.OrderBy(k => k).ToList(); } }

    public bool Exists(string name) { lock (_lock) return _roles.ContainsKey(Key(name)); }

    /// <summary>The permissions of a role; empty if there is no such role.</summary>
    public HashSet<string> PermissionsOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new HashSet<string>();
        lock (_lock) return _roles.TryGetValue(Key(name), out var set) ? new HashSet<string>(set) : new HashSet<string>();
    }

    public bool Create(string name)
    {
        lock (_lock)
        {
            if (_roles.ContainsKey(Key(name))) return false;
            _roles[Key(name)] = new SortedSet<string>(StringComparer.Ordinal);
            Save();
            return true;
        }
    }

    public bool Delete(string name)
    {
        lock (_lock)
        {
            if (!_roles.Remove(Key(name))) return false;
            Save();
            return true;
        }
    }

    /// <summary>Adds or removes one permission; false if the role does not exist or nothing changed.</summary>
    public bool Change(string name, string permission, bool add)
    {
        lock (_lock)
        {
            if (!_roles.TryGetValue(Key(name), out var set)) return false;
            bool changed = add ? set.Add(permission) : set.Remove(permission);
            if (changed) Save();
            return changed;
        }
    }

    private void Load()
    {
        if (_path == null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_path));
            if (loaded == null) return;
            _roles = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in loaded) _roles[Key(kv.Key)] = new SortedSet<string>(kv.Value ?? new List<string>(), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // A damaged file must not take the server down, nor be silently overwritten.
            Log.Error(ex, "RoleRepository: could not read {Path}; starting with no custom roles and keeping the file as .bad.", _path);
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch { }
        }
    }

    private void Save()
    {
        if (_path == null) return;
        string tmp = _path + ".tmp";
        var plain = _roles.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        File.WriteAllText(tmp, JsonSerializer.Serialize(plain, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _path, overwrite: true);
    }
}

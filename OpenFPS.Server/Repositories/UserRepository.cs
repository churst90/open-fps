using System.Text.Json;
using BCrypt.Net;
using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

public class UserData
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Player;

    // What the server knows about the account beyond the credential. Null where it was never recorded:
    // an account made before 2026-10-02 has no creation time, and one that has never logged in since
    // has no last login.
    public DateTime? CreatedUtc { get; set; }
    public DateTime? LastLoginUtc { get; set; }
    public string? LastLoginAddress { get; set; }
    /// <summary>Wrong passwords since the last successful login.</summary>
    public int FailedLogins { get; set; }
    public DateTime? LastFailedUtc { get; set; }
    public string? LastFailedAddress { get; set; }
    /// <summary>The name the player chose to show on their profile, if any.</summary>
    public string? RealName { get; set; }
}

public class UserRepository : IUserRepository
{
    private readonly string _filePath;
    private Dictionary<string, UserData> _users = new();
    private readonly object _lock = new();

    public UserRepository(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                AddUserInternal("admin", "admin123", UserRole.Admin);
                return;
            }

            string json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<UserData>>(json) ?? new();
            _users = list.ToDictionary(u => u.Username.Trim().ToLowerInvariant());
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            string json = JsonSerializer.Serialize(_users.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
    }

    public UserData? GetUser(string username)
    {
        lock (_lock)
        {
            return _users.GetValueOrDefault(username.Trim().ToLowerInvariant());
        }
    }

    public bool AddUser(string username, string password, UserRole role)
    {
        lock (_lock)
        {
            if (_users.ContainsKey(username.Trim().ToLowerInvariant())) return false;
            AddUserInternal(username, password, role);
            SaveInternal();
            return true;
        }
    }

    private void AddUserInternal(string username, string password, UserRole role)
    {
        var userData = new UserData
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role
        };
        _users[username.Trim().ToLowerInvariant()] = userData;
    }

    private void SaveInternal()
    {
        string json = JsonSerializer.Serialize(_users.Values.ToList(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_filePath, json);
    }

    public bool VerifyPassword(string username, string password)
    {
        var user = GetUser(username);
        if (user == null) return false;
        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
    }
}

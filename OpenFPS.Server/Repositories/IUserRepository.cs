using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// Defines the persistence contract for user credentials and roles.
/// Decoupled from the underlying storage mechanism to allow swapping implementations (flat-file, SQLite, etc.).
/// </summary>
public interface IUserRepository
{
    UserData? GetUser(string username);
    /// <summary>
    /// Creates a user. Returns false if the username is already taken — the caller is expected to tell
    /// the person the truth about that rather than reporting a success they cannot then log in with.
    /// </summary>
    bool AddUser(string username, string password, UserRole role);
    bool VerifyPassword(string username, string password);
}

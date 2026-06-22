using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// Defines the persistence contract for user credentials and roles.
/// Decoupled from the underlying storage mechanism to allow swapping implementations (flat-file, SQLite, etc.).
/// </summary>
public interface IUserRepository
{
    UserData? GetUser(string username);
    void AddUser(string username, string password, UserRole role);
    bool VerifyPassword(string username, string password);
}

using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// Defines the persistence contract for user credentials and roles.
/// Decoupled from the underlying storage mechanism to allow swapping implementations (flat-file, SQLite, etc.).
///
/// The members after <see cref="VerifyPassword"/> have bodies that do nothing, so a store that keeps
/// only names and hashes (the test stubs, the old JSON file) still satisfies the contract. The SQLite
/// store, which the server runs on, implements all of them.
/// </summary>
public interface IUserRepository
{
    UserData? GetUser(string username);
    /// <summary>
    /// Creates a user. Returns false if the username is already taken — the caller is expected to tell
    /// the person the truth about that rather than reporting a success they cannot then log in with.
    /// </summary>
    bool AddUser(string username, string password, UserRole role);
    /// <summary>
    /// Whether the password is right. An unknown name must take as long to answer as a known one, so
    /// the time a refusal takes does not say whether the account exists.
    /// </summary>
    bool VerifyPassword(string username, string password);

    /// <summary>A successful login: when, from where. Clears the failed-login count.</summary>
    void RecordLogin(string username, string address, DateTime utc) { }

    /// <summary>A wrong password for an account that exists: when, from where, and one more on the count.</summary>
    void RecordFailedLogin(string username, string address, DateTime utc) { }

    /// <summary>The name a player chose to show on their profile; null or empty clears it.</summary>
    bool SetRealName(string username, string? realName) => false;

    /// <summary>Changes a user's role. False if there is no such user.</summary>
    bool SetRole(string username, UserRole role) => false;

    /// <summary>Replaces a user's granted permissions (comma separated). False if there is no such user
    /// or this store cannot keep them.</summary>
    bool SetGrants(string username, string grants) => false;
}

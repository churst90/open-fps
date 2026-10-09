using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

/// <summary>
/// Accounts: credentials, roles and what a player leaves behind. The members after
/// <see cref="VerifyPassword"/> default to doing nothing, so a store that keeps only names and hashes
/// (the test stubs, the JSON file) still fits; the SQLite store the server runs on implements them all.
/// </summary>
public interface IUserRepository
{
    UserData? GetUser(string username);
    /// <summary>Creates a user. False if the name is taken, which the caller must say rather than
    /// report a success that cannot log in.</summary>
    bool AddUser(string username, string password, UserRole role);
    /// <summary>Whether the password is right. An unknown name must take as long to answer as a known
    /// one, so the time a refusal takes does not say whether the account exists.</summary>
    bool VerifyPassword(string username, string password);

    /// <summary>A successful login: when, from where. Clears the failed-login count.</summary>
    void RecordLogin(string username, string address, DateTime utc) { }

    /// <summary>A wrong password for an account that exists: when, from where, and one more on the count.</summary>
    void RecordFailedLogin(string username, string address, DateTime utc) { }

    /// <summary>The name a player chose to show on their profile; null or empty clears it.</summary>
    bool SetRealName(string username, string? realName) => false;

    /// <summary>Changes a user's role. False if there is no such user.</summary>
    bool SetRole(string username, UserRole role) => false;

    /// <summary>Everybody with this role. A store that cannot say returns none, so the last owner is
    /// never taken off by a store that cannot count them.</summary>
    IReadOnlyList<string> UsernamesWithRole(UserRole role) => Array.Empty<string>();

    /// <summary>Replaces a user's granted permissions (comma separated). False if there is no such user
    /// or this store cannot keep them.</summary>
    bool SetGrants(string username, string grants) => false;

    /// <summary>Sets or clears (null) a user's custom role. False if there is no such user.</summary>
    bool SetCustomRole(string username, string? role) => false;

    /// <summary>Takes a deleted custom role off everybody who had it; how many.</summary>
    int ClearCustomRole(string role) => 0;

    /// <summary>A player leaving the world: their state (place, health, stats) and what they carried,
    /// both JSON, written together. False if this store cannot keep them, and the caller then puts the
    /// things down rather than let them vanish.</summary>
    bool SavePlayer(string username, string? state, string? belongings) => false;

    /// <summary>What a player carried when they left, read and cleared in one go, so nothing can come
    /// back twice. Null for nothing.</summary>
    string? TakeBelongings(string username) => null;

    /// <summary>Bans an account until <paramref name="untilUtc"/>, or until lifted (null), in place of
    /// any ban it had. False if there is no such user or this store cannot keep bans.</summary>
    bool SetBan(string username, DateTime atUtc, DateTime? untilUtc, string by, string? reason) => false;

    /// <summary>Lifts an account's ban. False if there is no such user or it was not banned.</summary>
    bool ClearBan(string username) => false;

    /// <summary>Every account with a ban recorded, ended ones too: the caller lifts those.</summary>
    IReadOnlyList<UserData> Banned() => Array.Empty<UserData>();
}

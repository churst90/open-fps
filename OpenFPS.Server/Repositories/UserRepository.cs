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
    /// <summary>Single permissions granted on top of the role, comma separated (Permissions).</summary>
    public string? Permissions { get; set; }
}

using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

public class UserData
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Player;

    // Null where never recorded: an account made before 2026-10-02 has no creation time.
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
    /// <summary>A role an administrator made (RoleRepository), on top of Player. Null for none.</summary>
    public string? CustomRole { get; set; }
    /// <summary>Where the player last was, their health and their stats, as JSON (see Core.PlayerState).
    /// Null for an account that has never left the world since this was kept.</summary>
    public string? PlayerState { get; set; }
    /// <summary>What they carried when they last left the world, as JSON (see Core.Belongings), until
    /// they come back and it is taken out again.</summary>
    public string? Belongings { get; set; }
}

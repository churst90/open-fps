using OpenFPS.Common.Components;

namespace OpenFPS.Server.Repositories;

public class UserData
{
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Player;
}

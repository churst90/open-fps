using Arch.Core;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using System.Numerics;

namespace OpenFPS.Server.Core;

/// <summary>
/// Represents an active player session on the server, tracking their connection, 
/// current location, and input state.
/// </summary>
public class UserSession
{
    public int ConnectionId { get; set; }
    public string Username { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.Player;
    public string CurrentMapId { get; set; } = "default";
    public Entity Entity { get; set; } = Entity.Null;
    public long LastProcessedSequenceId { get; set; } = -1;
    public HashSet<int> KnownEntities { get; } = new();
    public ClientInputUpdate LastInput { get; set; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<ClientInputUpdate> InputQueue { get; } = new();
    public DateTime LastCollisionTime { get; set; } = DateTime.MinValue;
}

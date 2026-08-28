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

    /// <summary>
    /// Simulated seconds this player is still owed. Each tick grants one tick's worth (capped),
    /// and every input consumes what it claims — so a client cannot buy extra distance by sending
    /// inputs faster than real time, however large a DeltaTime it forges.
    /// </summary>
    public float InputBudget { get; set; }

    /// <summary>Inputs discarded because the queue was full — flood diagnostics.</summary>
    public long DroppedInputs { get; set; }

    public DateTime LastCollisionTime { get; set; } = DateTime.MinValue;
}

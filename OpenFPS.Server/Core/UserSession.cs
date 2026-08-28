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

    /// <summary>
    /// Entities this client has been sent a definition for. The server owns this: an entity it is not in
    /// here gets its definition before any state that references it, and an entity removed from the world
    /// is announced and struck off. Without it, remote players arrive as bare transforms with no
    /// definition — invisible to the client's snapshot — and disconnected players never leave.
    /// </summary>
    public HashSet<int> KnownEntities { get; } = new();

    /// <summary>
    /// The dynamic entities that were inside this client's area of interest last broadcast. Anything that
    /// drops out is announced as removed. Static geometry is deliberately NOT tracked here: the client
    /// builds its acoustic map from the whole streamed map, so evicting a distant wall would silently
    /// change how the world sounds.
    /// </summary>
    public HashSet<int> VisibleDynamicEntities { get; } = new();
    public ClientInputUpdate LastInput { get; set; } = new();
    public System.Collections.Concurrent.ConcurrentQueue<ClientInputUpdate> InputQueue { get; } = new();

    /// <summary>True for a MUD (telnet) session: no UDP peer, so no state stream and no voice.</summary>
    public bool IsTextClient { get; set; }

    /// <summary>
    /// Simulated seconds this player is still owed. Each tick grants one tick's worth (capped),
    /// and every input consumes what it claims — so a client cannot buy extra distance by sending
    /// inputs faster than real time, however large a DeltaTime it forges.
    /// </summary>
    public float InputBudget { get; set; }

    /// <summary>Inputs discarded because the queue was full — flood diagnostics.</summary>
    public long DroppedInputs { get; set; }

    /// <summary>
    /// This player's memory of where the floor was. The ground probe runs once per INPUT, and a player who
    /// sends several sub-tick inputs in a tick has barely moved between them, so the probe kept recomputing
    /// an answer it already had. Lives on the session so it is naturally per-player and disappears with the
    /// disconnect. See <see cref="OpenFPS.Common.GroundProbeMemo"/>.
    /// </summary>
    public OpenFPS.Common.GroundProbeMemo GroundProbe;

    public DateTime LastCollisionTime { get; set; } = DateTime.MinValue;
}

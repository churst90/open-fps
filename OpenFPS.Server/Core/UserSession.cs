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
    /// <summary>Single permissions given to this account on top of its role (/grant). See Permissions.</summary>
    public HashSet<string> Grants { get; set; } = new();
    /// <summary>Chat refused until then (/mute). Not kept past the session.</summary>
    public DateTime MutedUntilUtc { get; set; } = DateTime.MinValue;
    /// <summary>A role an administrator made, on top of Player (RoleRepository), and what it allows.</summary>
    public string CustomRole { get; set; } = "";
    public HashSet<string> RolePermissions { get; set; } = new();
    public bool Can(string permission) => Permissions.Has(Role, Grants, permission) || RolePermissions.Contains(permission);
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

    /// <summary>Aim assistance for a shot from the hip (CombatService.Assist): on unless the player turns
    /// it off. The client sends its saved choice when it enters the world, and /aimassist changes it.</summary>
    public bool AimAssist { get; set; } = true;

    /// <summary>Where the connection came from, as the transport reported it at login.</summary>
    public string RemoteAddress { get; set; } = "";

    /// <summary>When this session logged in.</summary>
    public DateTime LoggedInUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The last time the player did something: moved, looked, typed, spoke, used something. Input the
    /// client sends with nobody at the keys does not count.
    /// </summary>
    public DateTime LastActivityUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Set by /afk, cleared by the next thing the player does.</summary>
    public bool Away { get; set; }

    /// <summary>Idle this long and a profile says so, and everyone is told you are away.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(5);

    /// <summary>Away by /afk, or by having done nothing for <see cref="IdleAfter"/>.</summary>
    public bool IsAway(DateTime nowUtc) => Away || nowUtc - LastActivityUtc >= IdleAfter;

    /// <summary>
    /// Whether everyone was last told this player is away. Kept apart from <see cref="IsAway"/> so
    /// that each change is announced once: an idle player is away every second after the fifth
    /// minute, and must not be said to be away every second.
    /// </summary>
    public bool AnnouncedAway { get; set; }

    /// <summary>"online", "away", or "idle for N minutes" — what other players are told.</summary>
    public string Status(DateTime nowUtc)
    {
        if (Away) return "away";
        var idle = nowUtc - LastActivityUtc;
        if (idle < IdleAfter) return "online";
        int minutes = (int)idle.TotalMinutes;
        return $"idle for {minutes} minute{(minutes == 1 ? "" : "s")}";
    }

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

    /// <summary>
    /// Set once this session has had its first spawn and the message of the day with it. A change of
    /// map spawns you again, and the MOTD must not come round a second time.
    /// </summary>
    public bool Welcomed { get; set; }

    /// <summary>
    /// What the server keeps about this player between visits (where they were on each map, health,
    /// stats), read at login and written each time their body leaves the world (PlayerStore). Null
    /// until read; a test rig that never logs in leaves it so.
    /// </summary>
    public PlayerState? Saved { get; set; }

    /// <summary>
    /// Where this player is building, and what they have put there.
    ///
    /// Per-session and deliberately not persisted: a build cursor is a place you are working, like a
    /// caret, not a property of the world. It goes when you do.
    /// </summary>
    public BuildSession Build { get; } = new();
}

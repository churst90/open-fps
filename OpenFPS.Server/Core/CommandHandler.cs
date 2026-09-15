using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using OpenFPS.Server.Repositories;
using OpenFPS.Server.Systems;
using Arch.Core;
using Serilog;

namespace OpenFPS.Server.Core;

/// <summary>
/// Executes the text command set (/scan, /move, /spawn, …).
///
/// Two things about the shape of this class matter. First, it never touches a <see cref="LiteNetLib.NetPeer"/>:
/// it answers through a reply callback, so a telnet session and a UDP session are the same thing to it.
/// Second, every command body runs on the tick thread via the server's command buffer. The MUD gateway
/// dispatches from its own TCP task thread, and a command that mutated the Arch world from there would
/// race the simulation — the old peer-null guard prevented that race only by dropping every MUD command
/// on the floor, silently.
/// </summary>
public class CommandHandler
{
    private readonly SessionManager _sessions;
    private readonly MapManager _maps;
    private readonly GameServer _server;
    private readonly CompositeService? _composites;
    private readonly OccupancyService? _seats;

    public CommandHandler(SessionManager sessions, MapManager maps, GameServer server,
                          CompositeService? composites = null, OccupancyService? seats = null)
    {
        _sessions = sessions;
        _maps = maps;
        _server = server;
        _composites = composites;
        _seats = seats;
    }

    public void HandleTextCommand(int connectionId, TextCommand cmd, Action<IMessage> reply)
    {
        if (!_sessions.TryGetSession(connectionId, out var session))
        {
            Say(reply, "You are not logged in.");
            return;
        }

        string commandName = cmd.Command.TrimStart('/').ToLowerInvariant();
        string[] args = cmd.Args ?? Array.Empty<string>();
        bool isElevated = session.Role == UserRole.Dev || session.Role == UserRole.Admin;

        _server.EnqueueCommand(() => {
            try { Execute(commandName, args, session, reply, isElevated); }
            catch (Exception ex)
            {
                Log.Error(ex, "Error executing command '{Command}' for {User}", commandName, session.Username);
                Say(reply, $"Command '{commandName}' failed. The error has been logged.");
            }
        });
    }

    private void Execute(string commandName, string[] args, UserSession session, Action<IMessage> reply, bool isElevated)
    {
        switch (commandName)
        {
            case "scan": HandleScan(session, reply); break;
            case "pm": HandlePrivateMessage(session, args, reply); break;
            case "move":
            case "tp":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleMove(session, args, reply);
                break;
            case "spawn":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleSpawn(session, args, reply);
                break;
            case "set_sound":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleSetSound(session, args, reply);
                break;
            case "set_audio_mode":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleSetAudioMode(session, args, reply);
                break;
            case "play_folder":
                if (!isElevated) { DenyCommand(reply); return; }
                HandlePlayFolder(session, args, reply);
                break;
            case "start_state":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleStartState(session, args, reply);
                break;
            // ── Building ────────────────────────────────────────────────────────────────────
            //
            // The whole verb set for making something, and it is deliberately small: gather what is
            // around you into one thing, take it apart again, save it so it can be made again, put a
            // saved one down, and write the world to disk. A house, a market stall, a barricade and a
            // vehicle body are all the same five verbs.
            case "group":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleGroup(session, args, reply);
                break;
            case "ungroup":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleUngroup(session, args, reply);
                break;
            case "saveas":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleSaveAs(session, args, reply);
                break;
            case "place":
                if (!isElevated) { DenyCommand(reply); return; }
                HandlePlace(session, args, reply);
                break;
            case "composites":
                HandleListComposites(reply);
                break;
            // ── Placing things where you cannot point ───────────────────────────────────────
            case "origin":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleOrigin(session, reply);
                break;
            case "at":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleAt(session, args, reply);
                break;
            case "put":
                if (!isElevated) { DenyCommand(reply); return; }
                HandlePut(session, args, reply);
                break;
            case "undo":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleUndo(session, reply);
                break;
            case "room":
                HandleRoom(session, args, reply);
                break;
            case "prefabs":
                HandleListPrefabs(reply);
                break;
            // ── Doors ───────────────────────────────────────────────────────────────────────
            //
            // Not elevated. Building a door needs a role; going through one does not.
            case "open":
                HandleDoor(session, args, reply, open: true);
                break;
            case "close":
            case "shut":
                HandleDoor(session, args, reply, open: false);
                break;
            case "doors":
                HandleListDoors(session, reply);
                break;
            case "addseat":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleAddSeat(session, args, reply);
                break;
            case "removeseat":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleRemoveSeat(session, args, reply);
                break;
            case "drivable":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleDrivable(session, args, reply);
                break;
            // ── Occupancy ───────────────────────────────────────────────────────────────────
            //
            // Not elevated, any of it. Getting into things is what the world is FOR; building the
            // thing you get into is the part that needs a role.
            case "enter":
            case "board":
            case "getin":
                HandleEnter(session, args, reply);
                break;
            case "exit":
            case "getout":
                HandleExit(session, reply);
                break;
            case "seats":
                HandleSeats(session, reply);
                break;
            case "savemap":
                if (!isElevated) { DenyCommand(reply); return; }
                HandleSaveMap(session, reply);
                break;
            default:
                Say(reply, $"Command '{commandName}' not recognized.");
                break;
        }
    }

    private static void Say(Action<IMessage> reply, string text) => reply(new TextEvent { Text = text });

    private static void DenyCommand(Action<IMessage> reply) =>
        Say(reply, "You do not have permission to execute this command.");

    /// <summary>
    /// Resolves the session's map and its body in it. A session that has authenticated but not yet sent
    /// 'ready' has no entity, and every world command used to dereference it regardless.
    /// </summary>
    private bool TryGetBody(UserSession session, Action<IMessage> reply,
                            out World world, out SpatialGrid<Entity> grid, out Vector3 position)
    {
        world = null!; grid = null!; position = default;

        if (!_maps.TryGetMap(session.CurrentMapId, out world, out _, out grid, out _))
        {
            Say(reply, $"Map '{session.CurrentMapId}' is not loaded.");
            return false;
        }

        if (session.Entity == Entity.Null || !world.IsAlive(session.Entity) || !world.Has<Transform>(session.Entity))
        {
            Say(reply, "You are not in the world yet. Type 'ready' to enter it first.");
            return false;
        }

        position = world.Get<Transform>(session.Entity).Position;
        return true;
    }

    private void HandleSetAudioMode(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /set_audio_mode [LoopOne|LoopFolder|OneShot|StateMachine]"); return; }
        if (!Enum.TryParse<PlaybackMode>(args[0], true, out var mode)) { Say(reply, $"Unknown playback mode '{args[0]}'."); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null) { Say(reply, "No object nearby."); return; }

        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.Mode = mode;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Set audio mode of nearest object to {mode}.");
    }

    private void HandlePlayFolder(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 1) { Say(reply, "Usage: /play_folder [FolderId]"); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null) { Say(reply, "No object nearby."); return; }

        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.SoundId = args[0];
        emitter.Mode = PlaybackMode.LoopFolder;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Nearest object is now playing folder: {args[0]}.");
    }

    private void HandleStartState(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 2) { Say(reply, "Usage: /start_state [StartSoundId] [LoopSoundId]"); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null) { Say(reply, "No object nearby."); return; }

        var emitter = world.Has<SoundEmitterComponent>(nearest.Value) ? world.Get<SoundEmitterComponent>(nearest.Value) : new SoundEmitterComponent();
        emitter.StartSoundId = args[0];
        emitter.SoundId = args[1];
        emitter.Mode = PlaybackMode.StateMachine;
        SetOrAdd(world, nearest.Value, emitter);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Started state machine on nearest object: {args[0]} -> {args[1]}.");
    }

    private static void SetOrAdd<T>(World world, Entity e, T component) where T : struct
    {
        if (world.Has<T>(e)) world.Set(e, component);
        else world.Add(e, component);
    }

    private static Entity? FindNearestObject(World world, Entity self, Vector3 playerPos)
    {
        Entity? nearest = null;
        float minDist = PhysicsConstants.InteractionRange;
        world.Query(new QueryDescription().WithAll<Transform, IdentityComponent>(), (Entity e, ref Transform t) => {
            if (e == self) return;
            float d = Vector3.Distance(playerPos, t.Position);
            if (d < minDist) { minDist = d; nearest = e; }
        });
        return nearest;
    }

    private void HandleSpawn(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 5)
        {
            Say(reply, "Usage: /spawn [Box|Cylinder] [Material] [sizeX] [sizeY] [sizeZ]");
            return;
        }

        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        if (!Enum.TryParse<ColliderShape>(args[0], true, out var shape)) shape = ColliderShape.Box;
        string material = args[1];
        if (!float.TryParse(args[2], out float sx) || !float.TryParse(args[3], out float sy) || !float.TryParse(args[4], out float sz))
        {
            Say(reply, "Invalid sizes.");
            return;
        }

        var rot = world.Get<Transform>(session.Entity).Rotation;
        Vector3 forward = Vector3.Transform(new Vector3(0, 0, 1), rot);
        Vector3 spawnPos = playerPos + (forward * 3.0f);

        // Through the one spawn path: a bare world.Create left the object out of the map lookup and out
        // of the spatial grid, so nothing could see it, hear it or walk into it — while this command
        // cheerfully reported success.
        var e = _maps.SpawnEntity(session.CurrentMapId, w => w.Create(
            new Transform { Position = spawnPos, Rotation = Quaternion.Identity },
            new ColliderComponent { Shape = shape, Size = new Vector3(sx, sy, sz), IsSolid = true },
            new MaterialComponent { Material = material, Variant = "0" },
            new IdentityComponent { Name = $"Custom {shape}" },
            EntityType.StaticObject
        ));

        if (e == Entity.Null) { Say(reply, "Spawn failed: the map is not loaded."); return; }

        Say(reply, $"Spawned {material} {shape} (entity {e.Id}) at {spawnPos.X:F1}, {spawnPos.Y:F1}, {spawnPos.Z:F1}");
    }

    private void HandleSetSound(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 2)
        {
            Say(reply, "Usage: /set_sound [SoundId] [Volume]");
            return;
        }

        if (!float.TryParse(args[1], out float vol)) { Say(reply, $"'{args[1]}' is not a volume."); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        var nearest = FindNearestObject(world, session.Entity, playerPos);
        if (nearest == null)
        {
            Say(reply, "No object nearby to attach sound.");
            return;
        }

        // SetOrAdd, not Add: Add throws on an entity that already carries the component, which is what
        // every sibling handler here already knew.
        SetOrAdd(world, nearest.Value, new SoundEmitterComponent { SoundId = args[0], Volume = vol, Range = 50.0f });
        SetOrAdd(world, nearest.Value, EntityType.Beacon);
        _server.SyncAudioComponent(nearest.Value.Id);

        Say(reply, $"Attached sound {args[0]} to nearest object.");
    }

    private void HandleScan(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var playerPos)) return;

        float scanRadius = 20.0f;
        var results = new List<(string Name, float Distance, Vector3 Direction)>();

        world.Query(new QueryDescription().WithAll<Transform>(), (Entity e, ref Transform t) =>
        {
            if (e.Id == session.Entity.Id) return;

            float dist = Vector3.Distance(playerPos, t.Position);
            if (dist <= scanRadius && dist > 0.001f)
            {
                string name = "Unknown Object";
                if (world.Has<IdentityComponent>(e)) name = world.Get<IdentityComponent>(e).Name;
                else if (world.Has<PlayerComponent>(e)) name = world.Get<PlayerComponent>(e).Username;
                else if (world.Has<BeaconComponent>(e)) name = "Beacon";

                results.Add((name, dist, Vector3.Normalize(t.Position - playerPos)));
            }
        });

        if (results.Count == 0)
        {
            Say(reply, "No objects detected nearby.");
            return;
        }

        var playerRotation = world.Get<Transform>(session.Entity).Rotation;
        foreach (var item in results.OrderBy(r => r.Distance).Take(5))
        {
            string direction = GetRelativeDirection(playerRotation, item.Direction);
            Say(reply, $"{item.Name} at {direction}, {item.Distance:F1} meters.");
        }
    }

    // ── Building ────────────────────────────────────────────────────────────────────────────────

    /// <summary>How far a grouping sweep reaches by default, metres. About a room.</summary>
    private const float DefaultGroupRadius = 12f;
    /// <summary>How far away a composite may be and still count as "this one".</summary>
    private const float CompositeReachRadius = 30f;

    private CompositeService? Composites(Action<IMessage> reply)
    {
        if (_composites == null) Say(reply, "Building is not available on this server.");
        return _composites;
    }

    /// <summary>
    /// /group name [radius] [free] — makes one thing out of everything standing near you.
    ///
    /// A radius rather than a selection, because a selection needs pointing at things and pointing is
    /// the one thing a player here cannot do. "Everything within twelve metres of me" is a selection
    /// anybody can make, and can widen or narrow until it is the right one.
    /// </summary>
    private void HandleGroup(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /group name [radius] [free]"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        string name = args[0];
        float radius = args.Length > 1 && float.TryParse(args[1], out float r) ? r : DefaultGroupRadius;
        // "free" is the word for a composite that is not fixed down: a caravan rather than a house.
        bool anchored = !args.Contains("free", StringComparer.OrdinalIgnoreCase);

        int root = svc.Group(session.CurrentMapId, position, radius, name, anchored, session.Username, out int parts);
        if (root < 0) { Say(reply, $"Nothing within {radius:F0} m could be grouped."); return; }
        Say(reply, $"Grouped {parts} part(s) within {radius:F0} m into '{name}', "
                 + $"{(anchored ? "fixed in place" : "free to be moved")}, yours. Save it with /saveas.");
    }

    /// <summary>/ungroup — takes the nearest composite apart, leaving its parts exactly where they are.</summary>
    private void HandleUngroup(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m."); return; }
        if (!svc.Ungroup(session.CurrentMapId, root, session.Username, Elevated(session),
                         out string name, out int parts, out string error))
        { Say(reply, $"That could not be ungrouped: {error}."); return; }
        Say(reply, $"'{name}' is now {parts} loose part(s), all where they were.");
    }

    /// <summary>/saveas id — writes the nearest composite to disk so anyone can place it again.</summary>
    private void HandleSaveAs(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /saveas id"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m. Use /group first."); return; }
        if (!svc.SaveAsTemplate(session.CurrentMapId, root, args[0], session.Username, Elevated(session),
                                out int parts, out string error))
        { Say(reply, $"Could not save: {error}."); return; }
        Say(reply, $"Saved '{args[0]}' with {parts} part(s). Place another with /place {args[0]}.");
    }

    /// <summary>/place id [yaw] — puts a saved composite down at your feet, facing where you like.</summary>
    private void HandlePlace(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /place id [yaw degrees]"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        float yaw = args.Length > 1 && float.TryParse(args[1], out float y) ? y : 0f;
        var rotation = Quaternion.CreateFromYawPitchRoll(yaw * (MathF.PI / 180f), 0f, 0f);

        int root = svc.Place(session.CurrentMapId, args[0], position, rotation, session.Username,
                             out int parts, out string error);
        if (root < 0) { Say(reply, $"Could not place: {error}."); return; }
        Say(reply, $"Placed '{args[0]}' here, {parts} part(s), facing {yaw:F0} degrees. "
                 + "It will be gone after a restart until you /savemap.");
    }

    private static bool Elevated(UserSession session)
        => session.Role == UserRole.Dev || session.Role == UserRole.Admin;

    private OccupancyService? Seats(Action<IMessage> reply)
    {
        if (_seats == null) Say(reply, "Getting into things is not available on this server.");
        return _seats;
    }

    /// <summary>
    /// /addseat name [drive] — puts a seat where you are standing, facing the way you are facing.
    ///
    /// Authored by standing in the right place, for the same reason grouping is by radius: a player
    /// here cannot point at anything, but they can always walk to a spot and say "here".
    /// </summary>
    private void HandleAddSeat(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /addseat name [drive]"); return; }
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m. Use /group first."); return; }

        bool controls = args.Contains("drive", StringComparer.OrdinalIgnoreCase)
                     || args.Contains("driver", StringComparer.OrdinalIgnoreCase);
        float yaw = world.Has<PlayerComponent>(session.Entity) ? world.Get<PlayerComponent>(session.Entity).Yaw : 0f;

        if (!svc.AddSeat(session.CurrentMapId, root, args[0], controls, position, yaw,
                         session.Username, Elevated(session), out string error))
        { Say(reply, $"Could not add that seat: {error}."); return; }

        Say(reply, controls
            ? $"Seat '{args[0]}' added here, and it drives."
            : $"Seat '{args[0]}' added here.");
    }

    /// <summary>/removeseat name — forgets a seat, putting whoever is in it out first.</summary>
    private void HandleRemoveSeat(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1) { Say(reply, "Usage: /removeseat name"); return; }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m."); return; }
        if (!svc.RemoveSeat(session.CurrentMapId, root, args[0], session.Username, Elevated(session), out string error))
        { Say(reply, $"Could not remove that seat: {error}."); return; }
        Say(reply, $"Seat '{args[0]}' removed.");
    }

    /// <summary>
    /// /drivable preset — gives the nearest free composite an engine, tyres and a mass.
    ///
    /// The last step of turning a pile of walls into a car, and the shortest, because everything it
    /// needs already exists: the same vehicle profiles the map's own traffic runs on. From here the
    /// thing is a vehicle to every system that cares — the grid, the broadcast radius, the client's
    /// engine synthesis — none of which needs telling that a player built this one.
    /// </summary>
    private void HandleDrivable(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1)
        {
            Say(reply, $"Usage: /drivable preset. Known: {string.Join(", ", VehicleProfile.Presets.Keys)}");
            return;
        }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m."); return; }
        if (!svc.MakeDrivable(session.CurrentMapId, root, args[0], session.Username, Elevated(session), out string error))
        { Say(reply, $"Could not make that drivable: {error}."); return; }

        _server.SyncAudioComponent(root);
        var profile = VehicleProfile.ByName(args[0]);
        Say(reply, $"It drives as a {profile.Name} now — {profile.Engine.Name}, {profile.MassKg:F0} kg. "
                 + "Add a seat that drives with /addseat driver drive, then get in with /enter.");
    }

    /// <summary>/enter [seat] — gets into the nearest thing with seats.</summary>
    private void HandleEnter(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Seats(reply); if (svc == null) return;
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestEnterable(session.CurrentMapId, position, OccupancyService.BoardingRange);
        if (root < 0) { Say(reply, "There is nothing to get into within reach."); return; }

        svc.Enter(session, root, args.Length > 0 ? string.Join(" ", args) : null, out string message);
        Say(reply, message);
    }

    /// <summary>/exit — gets out, onto a clear patch of ground beside it.</summary>
    private void HandleExit(UserSession session, Action<IMessage> reply)
    {
        var svc = Seats(reply); if (svc == null) return;
        svc.Exit(session, out string message);
        Say(reply, message);
    }

    /// <summary>
    /// /seats — reads out what is inside the nearest thing you could get into.
    ///
    /// The replacement for looking through a window, and it has to say which seats are TAKEN as well
    /// as which exist: walking round a car trying doors is how a sighted player finds that out.
    /// </summary>
    private void HandleSeats(UserSession session, Action<IMessage> reply)
    {
        var svc = Seats(reply); if (svc == null) return;
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;

        int root = world.Has<OccupantComponent>(session.Entity)
            ? world.Get<OccupantComponent>(session.Entity).RootEntityId
            : svc.NearestEnterable(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, "Nothing with seats nearby."); return; }

        var seats = svc.Seats(session.CurrentMapId, root, position);
        if (seats.Count == 0) { Say(reply, "It has no seats."); return; }

        foreach (var seat in seats)
            Say(reply, $"  {seat.Name}{(seat.Controls ? " (drives)" : "")}: "
                     + $"{(seat.Taken ? "taken" : "free")}, {seat.Distance:F1} metres.");
    }

    // ── Building where you cannot point ─────────────────────────────────────────────────────────
    //
    // Walking to the spot works for a wall and not for a roof, and walking to twenty wall positions
    // in a row does not reliably produce a straight wall. So there is a CURSOR: moved in metres from
    // an origin the player chose, announcing itself and what is already there every time it moves.
    // That is a review cursor, which a screen-reader user has navigated documents with for years.

    /// <summary>/origin — puts the build origin at your feet, facing the way you face.</summary>
    private void HandleOrigin(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        float yaw = world.Has<PlayerComponent>(session.Entity) ? world.Get<PlayerComponent>(session.Entity).Yaw : 0f;
        session.Build.SetOrigin(position, yaw);
        Say(reply, $"Build origin set at your feet, forward is {Compass(yaw)}. The cursor is at the origin.");
    }

    /// <summary>
    /// /at [right up forward] — moves the cursor there and says what is there, or just reads it out.
    ///
    /// Also takes a named direction and a distance (`/at forward 3`), which is the form you want when
    /// you are running a wall: it moves RELATIVE to where the cursor already is, and remembers the
    /// direction so a `/put ... run` can follow it.
    /// </summary>
    private void HandleAt(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out var grid, out _)) return;
        var build = session.Build;
        if (!build.Placed) { Say(reply, "Set a build origin first with /origin."); return; }

        if (args.Length >= 2 && BuildSession.TryDirection(args[0], out var direction))
        {
            if (!float.TryParse(args[1], out float distance)) { Say(reply, $"'{args[1]}' is not a distance."); return; }
            build.Cursor += direction * distance;
            build.LastStep = direction;
        }
        else if (args.Length >= 3)
        {
            if (!float.TryParse(args[0], out float right) || !float.TryParse(args[1], out float up) || !float.TryParse(args[2], out float forward))
            { Say(reply, "Usage: /at right up forward, or /at <direction> <metres>"); return; }
            var moved = new Vector3(right, up, forward) - build.Cursor;
            if (moved.LengthSquared() > 0.0001f) build.LastStep = Vector3.Normalize(moved);
            build.Cursor = new Vector3(right, up, forward);
        }
        else if (args.Length != 0)
        {
            Say(reply, "Usage: /at right up forward, or /at <direction> <metres>, or /at on its own to read it out.");
            return;
        }

        Say(reply, $"Cursor {build.Describe()}. {WhatIsAt(world, grid, build.WorldCursor)}");
    }

    /// <summary>
    /// What is already where the cursor is.
    ///
    /// The half of the cursor that makes it usable. Moving to a spot and being told nothing is the
    /// same as not moving; being told "concrete wall" is how you find the wall you placed a minute
    /// ago and build the next one against it.
    /// </summary>
    private static string WhatIsAt(World world, SpatialGrid<Entity> grid, Vector3 at)
    {
        Entity? closest = null;
        float nearest = 1.5f;
        foreach (var e in grid.GetItemsInRadius(at, 3f))
        {
            if (!world.IsAlive(e) || !world.Has<Transform>(e) || world.Has<PlayerComponent>(e)) continue;
            float d = Vector3.Distance(world.Get<Transform>(e).Position, at);
            if (d < nearest) { nearest = d; closest = e; }
        }
        if (closest == null) return "Empty.";
        string name = world.Has<IdentityComponent>(closest.Value) ? world.Get<IdentityComponent>(closest.Value).Name
                    : world.Has<NameComponent>(closest.Value) ? world.Get<NameComponent>(closest.Value).Name
                    : "something";
        return nearest < 0.3f ? $"{name}, right here." : $"{name}, {nearest:F1} m off.";
    }

    /// <summary>
    /// /put prefab [turn degrees] [run n] — places a prefab at the cursor.
    ///
    /// The run is the important half. A wall is not one part, it is a line of them, and a line placed
    /// by hand from a cursor is only as straight as the arithmetic somebody did in their head. Asking
    /// for a run steps by the part's OWN footprint along the direction the cursor last travelled, so
    /// the panels touch, the wall is straight, and it is one command instead of eight.
    /// </summary>
    private void HandlePut(UserSession session, string[] args, Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        if (args.Length < 1)
        { Say(reply, "Usage: /put prefab [turn degrees] [run count [direction]]. /prefabs lists them."); return; }
        if (!TryGetBody(session, reply, out var world, out var grid, out _)) return;

        var build = session.Build;
        if (!build.Placed) { Say(reply, "Set a build origin first with /origin."); return; }

        string prefabId = args[0];
        if (!svc.Prefabs.Prefabs.ContainsKey(prefabId.ToLowerInvariant()))
        { Say(reply, $"There is no prefab called '{prefabId}'. /prefabs lists them."); return; }

        float turn = 0f;
        int run = 1;
        var step = build.LastStep;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("turn", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                float.TryParse(args[i + 1], out turn);
            else if (args[i].Equals("run", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                int.TryParse(args[i + 1], out run);
                // A direction on the run itself — "run 4 right" — which is what anybody would say.
                // Without it the only way to aim a run is to move the cursor zero metres in the
                // direction you want first, which works and is a riddle.
                if (i + 2 < args.Length && BuildSession.TryDirection(args[i + 2], out var aimed))
                {
                    step = aimed;
                    build.LastStep = aimed;
                }
            }
        }
        run = Math.Clamp(run, 1, 64);

        var rotation = build.Facing(turn);
        float spacing = FootprintAlong(svc.Prefabs, prefabId, rotation, build.Yaw, step);

        int placed = 0, blocked = 0;
        for (int i = 0; i < run; i++)
        {
            var local = build.Cursor + step * (spacing * i);
            var at = build.ToWorld(local);
            if (Occupied(world, grid, at, 0.3f)) { blocked++; continue; }

            Entity e;
            try { e = _maps.SpawnEntity(session.CurrentMapId, w => svc.Prefabs.Spawn(w, prefabId, at, rotation, Vector3.One)); }
            catch (Exception ex) { Say(reply, $"Could not place it: {ex.Message}"); return; }
            if (e == Entity.Null) { Say(reply, "Could not place it: the map is not loaded."); return; }
            build.Placed_Entities.Add(e.Id);
            placed++;
        }

        if (placed == 0) { Say(reply, "There is already something there. Nothing placed."); return; }
        // Leave the cursor at the end of the run, where the next thing goes.
        if (placed > 1) build.Cursor += step * (spacing * placed);

        string what = svc.Prefabs.Prefabs[prefabId.ToLowerInvariant()].Name;
        Say(reply, placed == 1
            ? $"{what} placed {build.Describe()}.{(blocked > 0 ? " Something was already there." : "")}"
            : $"{placed} x {what} placed in a line, {spacing:0.##} m apart. Cursor now {build.Describe()}."
              + (blocked > 0 ? $" {blocked} skipped, something was already there." : ""));
    }

    /// <summary>How far along a direction one of these reaches, so a run of them touches.</summary>
    private static float FootprintAlong(PrefabRepository prefabs, string prefabId, Quaternion rotation,
                                        float buildYaw, Vector3 stepInBuildAxes)
    {
        // A prefab need not have a body at all (a region volume, a bare emitter). One metre is a
        // sane pitch for a run of those, and the alternative — a run that never advances — would
        // stack the whole lot in one place with nothing to say why.
        var size = prefabs.Prefabs[prefabId.ToLowerInvariant()].ColliderSize ?? Vector3.One;
        if (size == Vector3.Zero) return 1f;
        // The part is rotated into the world; the step is in the builder's axes. Measure the part's
        // extent along the step by taking both into the same frame.
        var half = CompositeAcoustics.AxisAlignedHalfExtents(size * 0.5f,
            Quaternion.Concatenate(rotation, Quaternion.Inverse(Quaternion.CreateFromYawPitchRoll(buildYaw, 0f, 0f))));
        float along = MathF.Abs(stepInBuildAxes.X) * half.X
                    + MathF.Abs(stepInBuildAxes.Y) * half.Y
                    + MathF.Abs(stepInBuildAxes.Z) * half.Z;
        return MathF.Max(0.1f, along * 2f);
    }

    /// <summary>Whether something solid is already sitting where a part is about to go.</summary>
    private static bool Occupied(World world, SpatialGrid<Entity> grid, Vector3 at, float radius)
    {
        foreach (var e in grid.GetItemsInRadius(at, MathF.Max(radius, 1f)))
        {
            if (!world.IsAlive(e) || !world.Has<Transform>(e) || world.Has<PlayerComponent>(e)) continue;
            if (Vector3.Distance(world.Get<Transform>(e).Position, at) < radius) return true;
        }
        return false;
    }

    /// <summary>
    /// /undo — takes back the last thing you placed.
    ///
    /// Not a convenience. A part in the wrong place is invisible to somebody who cannot see it, so
    /// the mistake is not merely unfixed, it is undetectable until they walk into it — and by then
    /// they have built three more things around it.
    /// </summary>
    private void HandleUndo(UserSession session, Action<IMessage> reply)
    {
        var build = session.Build;
        if (!TryGetBody(session, reply, out var world, out _, out _)) return;

        while (build.Placed_Entities.Count > 0)
        {
            int id = build.Placed_Entities[^1];
            build.Placed_Entities.RemoveAt(build.Placed_Entities.Count - 1);
            if (!_maps.TryGetMap(session.CurrentMapId, out _, out _, out _, out var lookup)) break;
            if (!lookup.TryGetValue(id, out var e) || !world.IsAlive(e)) continue;   // already gone

            string what = world.Has<IdentityComponent>(e) ? world.Get<IdentityComponent>(e).Name : "it";
            _maps.DestroyEntity(session.CurrentMapId, e);
            _server.BroadcastRemoval(session.CurrentMapId, id);
            Say(reply, $"Took back the {what}.");
            return;
        }
        Say(reply, "You have not placed anything.");
    }

    /// <summary>
    /// /room [radius] — says whether what is around you encloses a room, and if not, what is missing.
    ///
    /// A dry run of the rule `/group` will apply, so it can be asked BEFORE committing. This is the
    /// feedback loop the whole of building without sight hangs off: a sighted builder stands back and
    /// sees that the roof is missing, and this is the replacement for standing back.
    /// </summary>
    private void HandleRoom(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        float radius = args.Length > 0 && float.TryParse(args[0], out float r) ? r : DefaultGroupRadius;

        // Exactly what a sweep of this radius would take, measured about where it would put the origin.
        var parts = new List<Entity>();
        var q = new QueryDescription().WithAll<Transform>();
        float r2 = radius * radius;
        world.Query(in q, (Entity e, ref Transform t) =>
        {
            if (Vector3.DistanceSquared(t.Position, position) > r2) return;
            if (!CompositeService.CanBeGrouped(world, e)) return;
            if (CompositeService.IsBiggerThanTheSweep(world, e, radius)) return;
            parts.Add(e);
        });

        if (parts.Count == 0) { Say(reply, $"Nothing within {radius:F0} m that could be built with."); return; }

        var survey = CompositeAcoustics.SurveyLoose(world, parts);
        Say(reply, $"{parts.Count} part(s) within {radius:F0} m. {survey.Explain()}");
    }

    /// <summary>/prefabs — what there is to put down.</summary>
    private void HandleListPrefabs(Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        var all = svc.Prefabs.Prefabs;
        if (all.Count == 0) { Say(reply, "No prefabs are loaded."); return; }
        Say(reply, $"{all.Count} prefab(s):");
        foreach (var kv in all)
        {
            var size = kv.Value.ColliderSize;
            Say(reply, $"  {kv.Key}: {kv.Value.Name}, {kv.Value.Material}"
                     + (size.HasValue ? $", {size.Value.X:0.##} by {size.Value.Y:0.##} by {size.Value.Z:0.##} m." : ", no body."));
        }
    }

    private static string Compass(float yaw)
    {
        float degrees = yaw * (180f / MathF.PI);
        while (degrees < 0) degrees += 360f;
        while (degrees >= 360f) degrees -= 360f;
        return degrees switch
        {
            < 22.5f or >= 337.5f => "north", < 67.5f => "north east", < 112.5f => "east",
            < 157.5f => "south east", < 202.5f => "south", < 247.5f => "south west",
            < 292.5f => "west", _ => "north west",
        };
    }

    // ── Doors ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How far away a door can be and still be one you can reach.
    ///
    /// The same range as every other interaction rather than a number of its own: a door is a thing
    /// you reach for, and there is no reason reaching for one should work at a different distance
    /// from reaching for anything else.
    /// </summary>
    private const float DoorReach = PhysicsConstants.InteractionRange;

    /// <summary>
    /// /open [name] — opens the door you meant, which is nearly always the nearest one.
    ///
    /// From a seat it is YOUR door: the one nearest the seat you are sitting in, which is the
    /// question "which door serves this seat" answered by proximity rather than by a table somebody
    /// has to author and keep in step. That is automatically right for a two-door, a four-door, a bus
    /// with a middle door, and whatever anybody invents next.
    /// </summary>
    private void HandleDoor(UserSession session, string[] args, Action<IMessage> reply, bool open)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;

        var from = ReachingFrom(world, session.Entity, position);
        string wanted = args.Length > 0 ? string.Join(" ", args) : "";

        var door = NearestDoor(world, from, wanted, out float distance, out string name);
        if (door == null)
        {
            // Say how far the nearest one actually is. "No door within five metres" while /doors is
            // cheerfully reporting one at five and a half is the tool contradicting itself, and the
            // player has no way to tell which of the two is lying.
            var anywhere = NearestDoor(world, from, wanted, out float away, out string itsName, reach: 40f);
            Say(reply, anywhere != null
                ? $"The nearest {itsName} is {away:F1} metres away. Get closer."
                : string.IsNullOrEmpty(wanted)
                    ? "There is no door anywhere near you."
                    : $"There is no door called '{wanted}' near you.");
            return;
        }

        var state = world.Get<DoorComponent>(door.Value);
        if (!DoorSystem.Set(world, door.Value, open))
        {
            Say(reply, state.Openness >= 1f ? $"The {name} is already open."
                     : state.Openness <= 0f ? $"The {name} is already shut."
                     : $"The {name} is already moving.");
            return;
        }
        Say(reply, $"The {name} swings {(open ? "open" : "shut")}, {distance:F1} metres away.");
    }

    /// <summary>
    /// /doors — what doors are near, how far, and whether they are open.
    ///
    /// The replacement for glancing round a room. It has to say the STATE as well as the name: an
    /// open door and a shut one in the same place are different facts, and the only other way to
    /// find out which you have is to walk into it.
    /// </summary>
    private void HandleListDoors(UserSession session, Action<IMessage> reply)
    {
        if (!TryGetBody(session, reply, out var world, out _, out var position)) return;
        var from = ReachingFrom(world, session.Entity, position);

        var found = new List<(string Name, float Distance, float Openness, string Direction)>();
        var rotation = world.Get<Transform>(session.Entity).Rotation;
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent d) =>
        {
            float distance = Vector3.Distance(from, t.Position);
            if (distance > 20f) return;
            var offset = t.Position - from;
            found.Add((DoorName(world, e), distance, d.Openness,
                       offset.LengthSquared() > 0.01f ? GetRelativeDirection(rotation, Vector3.Normalize(offset)) : "right here"));
        });

        if (found.Count == 0) { Say(reply, "No doors within twenty metres."); return; }
        foreach (var d in found.OrderBy(x => x.Distance))
            Say(reply, $"  {d.Name}: {(d.Openness >= 1f ? "open" : d.Openness <= 0f ? "shut" : $"{d.Openness * 100f:F0} per cent open")}, "
                     + $"{d.Direction}, {d.Distance:F1} metres.");
    }

    /// <summary>
    /// Where a player is reaching from — their seat if they are in one, otherwise their feet.
    ///
    /// Sitting in a car, the door you mean is the one beside YOU, not the one nearest the middle of
    /// the vehicle. On a bus those are different doors.
    /// </summary>
    private static Vector3 ReachingFrom(World world, Entity player, Vector3 position)
        => world.Has<OccupantComponent>(player) && world.Has<Transform>(player)
            ? world.Get<Transform>(player).Position
            : position;

    private static Entity? NearestDoor(World world, Vector3 from, string named,
                                       out float distance, out string name, float? reach = null)
    {
        Entity? best = null;
        float bestDistance = reach ?? (string.IsNullOrEmpty(named) ? DoorReach : 20f);
        string bestName = "door";
        world.Query(new QueryDescription().WithAll<Transform, DoorComponent>(), (Entity e, ref Transform t, ref DoorComponent _) =>
        {
            string thisName = DoorName(world, e);
            if (!string.IsNullOrEmpty(named)
                && thisName.IndexOf(named, StringComparison.OrdinalIgnoreCase) < 0) return;
            float d = Vector3.Distance(from, t.Position);
            if (d > bestDistance) return;
            bestDistance = d; best = e; bestName = thisName;
        });
        distance = bestDistance; name = bestName;
        return best;
    }

    private static string DoorName(World world, Entity e)
        => world.Has<IdentityComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<IdentityComponent>(e).Name)
            ? world.Get<IdentityComponent>(e).Name
            : world.Has<NameComponent>(e) && !string.IsNullOrWhiteSpace(world.Get<NameComponent>(e).Name)
                ? world.Get<NameComponent>(e).Name
                : "door";

    /// <summary>/composites — what there is to place.</summary>
    private void HandleListComposites(Action<IMessage> reply)
    {
        var svc = Composites(reply); if (svc == null) return;
        var all = svc.Templates.All;
        if (all.Count == 0) { Say(reply, "Nothing has been saved yet. Build something and use /group then /saveas."); return; }
        Say(reply, $"{all.Count} composite(s) available:");
        foreach (var kv in all)
            Say(reply, $"  {kv.Key}: {kv.Value.Name}, {kv.Value.Parts.Count} part(s), "
                     + $"{(kv.Value.Anchored ? "fixed" : "free")}.");
    }

    /// <summary>
    /// /savemap — writes this map to disk exactly as it now stands.
    ///
    /// Explicit rather than automatic on purpose. A world that rewrites its own map file every time
    /// somebody experiments cannot be experimented with, and the first thing anyone does with a
    /// building tool is put something in the wrong place.
    /// </summary>
    private void HandleSaveMap(UserSession session, Action<IMessage> reply)
    {
        if (!_maps.SaveMap(session.CurrentMapId, out string error))
        { Say(reply, $"Could not save the map: {error}."); return; }
        Say(reply, $"Map '{session.CurrentMapId}' saved. Anything you placed is now permanent.");
    }

    private void HandleMove(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 3)
        {
            Say(reply, "Usage: /move x y z");
            return;
        }

        if (!float.TryParse(args[0], out float x) || !float.TryParse(args[1], out float y) || !float.TryParse(args[2], out float z))
        {
            Say(reply, "Usage: /move x y z");
            return;
        }

        if (!TryGetBody(session, reply, out var world, out var grid, out _)) return;

        Vector3 targetPos = new Vector3(x, y, z);

        // COLLISION AWARE TELEPORT (Cylinder-based)
        if (OpenFPS.Server.Systems.MovementSystem.CheckCollision(world, grid, targetPos, PhysicsConstants.PlayerRadius, PhysicsConstants.PlayerHeight))
        {
            Say(reply, "Cannot move there: Area is solid.");
            return;
        }

        ref var t = ref world.Get<Transform>(session.Entity);
        t.Position = targetPos;
        t.IsDirty = true;

        // Force client reset
        reply(new PlayerSpawned { EntityId = session.Entity.Id, SpawnTransform = t });
        Say(reply, $"Moved to {targetPos.X:F1}, {targetPos.Y:F1}, {targetPos.Z:F1}");
    }

    private void HandlePrivateMessage(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (args.Length < 2)
        {
            Say(reply, "Usage: /pm [username] [message]");
            return;
        }

        string targetUsername = args[0];
        string message = string.Join(" ", args.Skip(1));

        var targetSession = _sessions.GetAllSessions().FirstOrDefault(s => s.Username.Equals(targetUsername, StringComparison.OrdinalIgnoreCase));
        if (targetSession == null)
        {
            Say(reply, $"User '{targetUsername}' not found.");
            return;
        }

        _server.SendToSession(targetSession, new ChatMessage { Sender = $"[PM from {session.Username}]", Text = message });
        reply(new ChatMessage { Sender = $"[PM to {targetUsername}]", Text = message });
    }

    private static string GetRelativeDirection(Quaternion rotation, Vector3 targetDir)
    {
        Matrix4x4 rotMat = Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(rotation));
        Vector3 localDir = Vector3.Transform(targetDir, rotMat);
        float angle = MathF.Atan2(localDir.X, localDir.Z) * (180.0f / MathF.PI);
        if (angle < 0) angle += 360.0f;
        return angle switch
        {
            < 22.5f or >= 337.5f => "12 o'clock", < 67.5f => "2 o'clock", < 112.5f => "3 o'clock",
            < 157.5f => "5 o'clock", < 202.5f => "6 o'clock", < 247.5f => "8 o'clock",
            < 292.5f => "9 o'clock", _ => "11 o'clock"
        };
    }
}

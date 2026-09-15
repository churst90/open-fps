using System.Linq;
using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
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

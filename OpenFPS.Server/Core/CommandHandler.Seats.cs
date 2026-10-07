using OpenFPS.Common;
using OpenFPS.Common.Networking;
using OpenFPS.Common.Components;
using OpenFPS.Server.Systems;
using Arch.Core;

namespace OpenFPS.Server.Core;

/// <summary>Seats, getting in and out, and what the driver works: the key, the siren, the horn, the windows.</summary>
public partial class CommandHandler
{
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
            Say(reply, $"Usage: /drivable preset. Known: {string.Join(", ", MachineRegistry.Ids)}");
            return;
        }
        if (!TryGetBody(session, reply, out _, out _, out var position)) return;

        int root = svc.NearestRoot(session.CurrentMapId, position, CompositeReachRadius);
        if (root < 0) { Say(reply, $"No composite within {CompositeReachRadius:F0} m."); return; }
        if (!svc.MakeDrivable(session.CurrentMapId, root, args[0], session.Username, Elevated(session), out string error))
        { Say(reply, $"Could not make that drivable: {error}."); return; }

        _server.SyncAudioComponent(root);
        var profile = MachineRegistry.VehicleFor(args[0]);
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

    /// <summary>
    /// /ignition [on|off] — the key, from the driver's seat. No argument turns it the other way from
    /// wherever it is, which is what a key does.
    /// </summary>
    private void HandleIgnition(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out var lookup)
            || session.Entity == Entity.Null || !world.IsAlive(session.Entity))
        { Say(reply, "You are not in the world yet."); return; }
        if (!world.Has<OccupantComponent>(session.Entity))
        { Say(reply, "You are not sitting in anything."); return; }
        var occupant = world.Get<OccupantComponent>(session.Entity);
        if (!occupant.Controls) { Say(reply, "The key is in front of the driver's seat."); return; }
        if (!lookup.TryGetValue(occupant.RootEntityId, out var root) || !world.IsAlive(root))
        { Say(reply, "There is nothing here to start."); return; }
        bool on = world.Has<DriveComponent>(root) && !world.Get<DriveComponent>(root).EngineOn;
        if (args.Length > 0) on = !args[0].Equals("off", StringComparison.OrdinalIgnoreCase);
        Say(reply, DrivingSystem.SetIgnition(world, root, on, _server.SyncAudioComponent));
    }

    /// <summary>The vehicle whose driving seat this session is in, or why not.</summary>
    private bool DrivenVehicle(UserSession session, Action<IMessage> reply, out World world, out Entity root)
    {
        root = Entity.Null;
        if (!_maps.TryGetMap(session.CurrentMapId, out world!, out _, out _, out var lookup)
            || session.Entity == Entity.Null || !world.IsAlive(session.Entity))
        { Say(reply, "You are not in the world yet."); return false; }
        if (!world.Has<OccupantComponent>(session.Entity))
        { Say(reply, "You are not sitting in anything."); return false; }
        var occupant = world.Get<OccupantComponent>(session.Entity);
        if (!occupant.Controls) { Say(reply, "Only the driver can do that."); return false; }
        if (!lookup.TryGetValue(occupant.RootEntityId, out root) || !world.IsAlive(root))
        { Say(reply, "There is nothing here to drive."); return false; }
        return true;
    }

    /// <summary>/siren [on|off|wail|yelp|phaser|hilo|next] — U and Shift+U in the driver's seat.</summary>
    private void HandleSiren(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!DrivenVehicle(session, reply, out var world, out var root)) return;
        Say(reply, VehicleSignals.SirenCommand(world, root, args));
    }

    /// <summary>/horn — a short blast, for a session that cannot hold H down.</summary>
    private void HandleHorn(UserSession session, Action<IMessage> reply)
    {
        if (!DrivenVehicle(session, reply, out var world, out var root)) return;
        if (!world.Has<DriveComponent>(root)) { Say(reply, "This has no horn."); return; }
        VehicleSignals.Tap(root.Id);
    }

    /// <summary>
    /// /window [down|up|half] — the side windows, from any seat. No argument rolls them the other way
    /// from wherever they are going, which is what R does in a vehicle.
    /// </summary>
    private void HandleWindow(UserSession session, string[] args, Action<IMessage> reply)
    {
        if (!_maps.TryGetMap(session.CurrentMapId, out var world, out _, out _, out var lookup)
            || session.Entity == Entity.Null || !world.IsAlive(session.Entity))
        { Say(reply, "You are not in the world yet."); return; }
        string mapId = session.CurrentMapId;
        Say(reply, WindowSystem.Command(world, session.Entity, lookup, args, _server.SyncAudioComponent,
                                        (id, label, sounds) => _server.EmitWorldAudio(mapId, id, label, sounds)));
    }
}

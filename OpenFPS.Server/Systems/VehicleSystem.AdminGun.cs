namespace OpenFPS.Server.Systems;

/// <summary>What the admin gun needs of the traffic: whether a thing is moved by it (so it can be
/// frozen), and to forget one that is about to be vaporized.</summary>
public sealed partial class VehicleSystem
{
    /// <summary>Whether the traffic moves this entity on this map: a vehicle on its route or somebody walking.</summary>
    public bool Moves(string mapId, int entityId) => _byEntity.ContainsKey((mapId, entityId));

    /// <summary>Stops moving an entity that is about to be removed. True if it was one of ours.</summary>
    public bool Forget(string mapId, int entityId)
    {
        if (!_byEntity.Remove((mapId, entityId), out var v)) return false;
        _vehicles.Remove(v);
        // A car gone from the kerb frees the door its driver went in by.
        if (v.Park is { } pk) _doorsInUse.Remove((mapId, pk.Spot.Door.Id));
        return true;
    }
}

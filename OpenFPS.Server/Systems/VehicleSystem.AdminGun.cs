namespace OpenFPS.Server.Systems;

/// <summary>What the admin gun needs of the traffic: whether a thing is moved by it (so it can be
/// frozen), and to forget one that is about to be vaporized.</summary>
public sealed partial class VehicleSystem
{
    /// <summary>Whether the traffic moves this entity: a vehicle on its route or somebody walking.</summary>
    public bool Moves(int entityId) => _byEntity.ContainsKey(entityId);

    /// <summary>Stops moving an entity that is about to be removed. True if it was one of ours.</summary>
    public bool Forget(string mapId, int entityId)
    {
        if (!_byEntity.TryGetValue(entityId, out var v) || v.MapId != mapId) return false;
        _byEntity.Remove(entityId);
        _vehicles.Remove(v);
        return true;
    }
}

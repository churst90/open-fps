using System.Numerics;
using System.Collections.Generic;
using System.Collections.Concurrent;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common;
using System.Linq;
using System;

namespace OpenFPS.Client.Core;

/// <summary>
/// Responsibility: Single source of truth for the client's world data.
/// Manages the transition from network messages to physical simulation.
/// </summary>
public class ClientWorldState
{
    private readonly ConcurrentDictionary<int, EntityDefinition> _definitions = new();
    private readonly ConcurrentDictionary<int, Transform> _serverTransforms = new();
    private readonly ConcurrentDictionary<int, Vector3> _serverVelocities = new();
    private readonly ConcurrentDictionary<int, byte> _audioEntityIds = new();

    // --- Snapshot Interpolation ---
    private readonly List<ServerStateUpdate> _snapshotBuffer = new();
    private const double InterpolationDelay = 0.1; // 100ms buffer
    private double _clientInterpolationTime = 0;
    
    private readonly object _gridLock = new();
    private SpatialGrid<int>? _staticGrid;
    private bool _gridNeedsRebuild = false;

    private readonly object _metaLock = new();
    public AcousticMap? AcousticMap { get; private set; }
    public Vector3 CurrentMapSize { get; private set; } = new Vector3(200, 100, 200);

    // Atmospheric State
    private readonly object _envLock = new();
    private WorldEnvironmentComponent _env = new();

    public float CurrentPrecipitation { get { lock(_envLock) return _env.PrecipitationIntensity; } }
    public float CurrentTemperature { get { lock(_envLock) return _env.Temperature; } }

    public void UpdateAtmosphere(WorldStateUpdate update)
    {
        lock (_envLock)
        {
            _env.Temperature = update.Temperature;
            _env.Humidity = update.Humidity;
            _env.AirPressure = update.AirPressure;
            _env.AirAbsorptionMultiplier = update.AirAbsorptionMultiplier;
            _env.WindVelocity = update.WindVelocity;
            _env.WindGustiness = update.WindGustiness;
            _env.PrecipitationIntensity = update.PrecipitationIntensity;
        }
    }

    public void Clear(Vector3 size, Vector3 minBound, Vector3 maxBound)
    {
        _definitions.Clear();
        _serverTransforms.Clear();
        _serverVelocities.Clear();
        _audioEntityIds.Clear();
        
        lock (_metaLock)
        {
            CurrentMapSize = size;
        }

        lock (_gridLock)
        {
            _staticGrid = new SpatialGrid<int>(
                new Vector2(minBound.X, minBound.Z),
                new Vector2(maxBound.X, maxBound.Z),
                10.0f);
            _gridNeedsRebuild = true;
        }
    }

    public void SetAcousticMap(AcousticMap map)
    {
        lock (_metaLock)
        {
            AcousticMap = map;
        }
    }

    public void RegisterDefinition(EntityDefinition def)
    {
        _definitions[def.EntityId] = def;
        _serverTransforms[def.EntityId] = def.Transform;

        if (def.SoundEmitter.Mode == PlaybackMode.LoopOne || def.SoundEmitter.IsSynth)
            _audioEntityIds[def.EntityId] = 0;

        lock (_gridLock)
        {
            _gridNeedsRebuild = true;
        }
    }

    /// <summary>
    /// Purges entities the server says are gone (destroyed, or out of our area of interest).
    /// Everything else about an entity is additive — a definition arrives and stays — so without this
    /// a disconnected player's body remains forever: still colliding, still announced by scans, still
    /// emitting whatever sound it carried. Returns the ids that were actually being tracked, so the
    /// caller can stop their voices.
    /// </summary>
    public List<int> RemoveEntities(IEnumerable<int> entityIds)
    {
        var removed = new List<int>();
        foreach (int id in entityIds)
        {
            bool known = _definitions.TryRemove(id, out _);
            known |= _serverTransforms.TryRemove(id, out _);
            _serverVelocities.TryRemove(id, out _);
            _audioEntityIds.TryRemove(id, out _);
            if (known) removed.Add(id);
        }

        if (removed.Count > 0)
        {
            lock (_gridLock)
            {
                _gridNeedsRebuild = true;
            }
        }
        return removed;
    }

    public void SyncState(ServerStateUpdate update)
    {
        lock (_snapshotBuffer)
        {
            _snapshotBuffer.Add(update);
            if (_snapshotBuffer.Count > 10) _snapshotBuffer.RemoveAt(0); // Prune old history
            _snapshotBuffer.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        }
    }

    /// <summary>
    /// Smooths remote entities by lerping between buffered server snapshots.
    /// Called once per game loop.
    /// </summary>
    public void UpdateInterpolation(float dt, int localPlayerId)
    {
        if (_snapshotBuffer.Count < 2) return;

        lock (_snapshotBuffer)
        {
            // 1. Determine the 'Playback Time' (current server tick we want to show)
            // We lag behind the latest received tick by InterpolationDelay seconds.
            double latestServerTime = _snapshotBuffer.Last().Tick * PhysicsConstants.FixedDeltaTime;
            if (_clientInterpolationTime == 0) _clientInterpolationTime = latestServerTime - InterpolationDelay;
            
            _clientInterpolationTime += dt;

            // 2. Find the two snapshots that bracket our playback time
            ServerStateUpdate? from = null;
            ServerStateUpdate? to = null;

            for (int i = 0; i < _snapshotBuffer.Count - 1; i++)
            {
                double t0 = _snapshotBuffer[i].Tick * PhysicsConstants.FixedDeltaTime;
                double t1 = _snapshotBuffer[i+1].Tick * PhysicsConstants.FixedDeltaTime;

                if (_clientInterpolationTime >= t0 && _clientInterpolationTime <= t1)
                {
                    from = _snapshotBuffer[i];
                    to = _snapshotBuffer[i+1];
                    break;
                }
            }

            if (from != null && to != null)
            {
                double t0 = from.Tick * PhysicsConstants.FixedDeltaTime;
                double t1 = to.Tick * PhysicsConstants.FixedDeltaTime;
                float alpha = (float)((_clientInterpolationTime - t0) / (t1 - t0));

                // 3. Perform Linear Interpolation for all dynamic entities
                foreach (var stateTo in to.States)
                {
                    if (stateTo.EntityId == localPlayerId) continue; // Skip ourselves (handled by CSP)

                    var stateFrom = from.States.FirstOrDefault(s => s.EntityId == stateTo.EntityId);
                    if (stateFrom.EntityId != 0)
                    {
                        var transFrom = stateFrom.Transform.ToTransform();
                        var transTo = stateTo.Transform.ToTransform();

                        var lerpedPos = Vector3.Lerp(transFrom.Position, transTo.Position, alpha);
                        var lerpedRot = Quaternion.Slerp(transFrom.Rotation, transTo.Rotation, alpha);

                        _serverTransforms[stateTo.EntityId] = new Transform { Position = lerpedPos, Rotation = lerpedRot };
                        _serverVelocities[stateTo.EntityId] = Vector3.Lerp(stateFrom.LinearVelocity, stateTo.LinearVelocity, alpha);
                    }
                    else
                    {
                        // Fallback if entity is missing from 'from' snapshot
                        _serverTransforms[stateTo.EntityId] = stateTo.Transform.ToTransform();
                        _serverVelocities[stateTo.EntityId] = stateTo.LinearVelocity;
                    }
                }
            }
        }
    }

    public void SyncState(IEnumerable<EntityState> states)
    {
        foreach (var s in states)
        {
            _serverTransforms[s.EntityId] = s.Transform.ToTransform();
            _serverVelocities[s.EntityId] = s.LinearVelocity;
        }
    }

    /// <summary>
    /// Generates a thread-safe copy of the world state for the simulation and audio threads.
    /// </summary>
    public WorldSnapshot GetSnapshot()
    {
        SpatialGrid<int>? gridCopy;
        lock (_gridLock)
        {
            if (_gridNeedsRebuild && _staticGrid != null) RebuildGrid();
            gridCopy = _staticGrid;
        }

        var snap = new WorldSnapshot
        {
            StaticGrid = gridCopy,
            AcousticMap = AcousticMap
        };

        lock (_envLock)
        {
            snap.Temperature = _env.Temperature;
            snap.Humidity = _env.Humidity;
            snap.AirPressure = _env.AirPressure;
            snap.AirAbsorptionMultiplier = _env.AirAbsorptionMultiplier;
            snap.WindVelocity = _env.WindVelocity;
            snap.WindGustiness = _env.WindGustiness;
            snap.PrecipitationIntensity = _env.PrecipitationIntensity;
        }

        foreach (var kvp in _definitions)
        {
            var id = kvp.Key;
            var s = new EntitySnapshot
            {
                Id = id,
                Definition = kvp.Value,
                Transform = _serverTransforms.GetValueOrDefault(id, kvp.Value.Transform),
                Velocity = _serverVelocities.GetValueOrDefault(id, Vector3.Zero)
            };
            snap.Entities[id] = s;
            if (s.Definition.Type != EntityType.StaticObject) snap.DynamicEntities.Add(s);
        }

        snap.AudioEntityIds.AddRange(_audioEntityIds.Keys);
        return snap;
    }

    private void RebuildGrid()
    {
        if (_staticGrid == null) return;
        _staticGrid.ClearAll(); // Clear both static and dynamic just in case, though it's the static grid.
        foreach (var kvp in _definitions)
        {
            if (kvp.Value.Type == EntityType.StaticObject && kvp.Value.Collider.IsSolid)
            {
                _staticGrid.AddOverlapping(kvp.Value.Transform.Position, kvp.Value.Collider.Size, kvp.Key, isStatic: true);
            }
        }
        _gridNeedsRebuild = false;
    }

    public int? GetClosestEntityId(Vector3 pos)
    {
        int? bestId = null;
        float bestDist = float.MaxValue;

        foreach (var kvp in _definitions)
        {
            if (kvp.Value.Type == EntityType.Player) continue;
            var transform = _serverTransforms.GetValueOrDefault(kvp.Key);
            float d = Vector3.Distance(pos, transform.Position);
            if (d < bestDist) { bestDist = d; bestId = kvp.Key; }
        }
        return bestDist < 3.0f ? bestId : null;
    }
}

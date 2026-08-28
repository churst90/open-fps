using System.Numerics;
using System.Collections.Generic;
using System.Collections.Concurrent;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common;
using System.Linq;
using System;
using System.Threading;

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

    // --- Snapshot cache -------------------------------------------------------------------------------
    // A snapshot is a full copy of the world: every definition, every transform, a dictionary and two lists.
    // A frame used to build between three and six of them — the predictor asked for one, the shelter check
    // asked for another, the proximity scan a third, the audio system a fourth — all describing the same
    // instant. They are read-only once built, so there is no reason for more than one to exist per version
    // of the world.
    //
    // Every mutation bumps _version. GetSnapshot hands back the cached copy while the version it was built
    // at still stands, and builds a new one when it does not. The pair is stored as a single immutable
    // object so a reader can never see a new version stamped on an old copy.
    private sealed record CachedSnapshot(long Version, WorldSnapshot Snapshot);
    private readonly Dictionary<int, EntityState> _interpolationFrom = new();
    private readonly object _snapshotLock = new();
    private volatile CachedSnapshot? _cached;
    private long _version;
    private long _snapshotBuilds;

    /// <summary>Snapshots actually built (as opposed to served from the cache). Diagnostic; used by tests
    /// to prove the frame builds one.</summary>
    public long SnapshotBuilds => Interlocked.Read(ref _snapshotBuilds);

    /// <summary>The current world version. Changes on every mutation; a snapshot is valid for exactly one.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <summary>
    /// How many entities the client knows about, without building a snapshot to count them. Map load
    /// reports progress on every definition that arrives, and asking for a snapshot to do it made the
    /// load quadratic: a full copy of the world, per entity of the world.
    /// </summary>
    public int EntityCount => _definitions.Count;

    /// <summary>Marks the world changed, so the next <see cref="GetSnapshot"/> rebuilds.</summary>
    private void Touch() => Interlocked.Increment(ref _version);

    public float CurrentPrecipitation { get { lock(_envLock) return _env.PrecipitationIntensity; } }
    public float CurrentTemperature { get { lock(_envLock) return _env.Temperature; } }
    public float CurrentWindGustiness { get { lock(_envLock) return _env.WindGustiness; } }
    public Vector3 CurrentWindVelocity { get { lock(_envLock) return _env.WindVelocity; } }

    public void UpdateAtmosphere(WorldStateUpdate update)
    {
        lock (_envLock)
        {
            _env.Temperature = update.Temperature;
            _env.Humidity = update.Humidity;
            _env.AirPressure = SanePressure(update.AirPressure);
            _env.AirAbsorptionMultiplier = SaneAbsorptionMultiplier(update.AirAbsorptionMultiplier);
            _env.WindVelocity = update.WindVelocity;
            _env.WindGustiness = Math.Clamp(update.WindGustiness, 0f, 1f);
            _env.PrecipitationIntensity = update.PrecipitationIntensity;
        }
        Touch();
    }

    /// <summary>
    /// Applies the map's authored atmosphere the moment the manifest lands.
    ///
    /// The world state broadcast arrives once a second, so without this the first second in a new map
    /// is heard through whatever the previous map's air was — or, on the first map, through the
    /// defaults. Everything here is overwritten by the next <see cref="UpdateAtmosphere"/>; it exists
    /// so the gap is authored rather than arbitrary.
    /// </summary>
    public void ApplyManifestAtmosphere(MapManifest manifest)
    {
        lock (_envLock)
        {
            _env.Temperature = manifest.Temperature;
            _env.Humidity = Math.Clamp(manifest.Humidity, 0f, 1f);
            _env.AirPressure = SanePressure(manifest.AirPressure);
            _env.AirAbsorptionMultiplier = SaneAbsorptionMultiplier(manifest.AirAbsorptionMultiplier);
        }
        Touch();
    }

    // A server that sends nonsense (an old build, a map authored in atmospheres, an unassigned field)
    // must not be able to switch the acoustics off from a distance. Both guards substitute the neutral
    // value rather than letting a zero propagate into a divisor.
    private static float SanePressure(float mb) => mb is > 300f and < 1100f ? mb : 1013.25f;
    private static float SaneAbsorptionMultiplier(float m) => m > 0.01f ? m : 1.0f;

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

        Touch();
    }

    public void SetAcousticMap(AcousticMap map)
    {
        lock (_metaLock)
        {
            AcousticMap = map;
        }
        Touch();
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

        Touch();
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
            Touch();
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

        bool moved = false;
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

                // Index the 'from' states once. The inner FirstOrDefault this replaces made the whole
                // interpolation quadratic in the entity count and allocated a lambda closure per entity,
                // every frame, for a lookup a dictionary answers in one step.
                _interpolationFrom.Clear();
                foreach (var stateFrom in from.States) _interpolationFrom[stateFrom.EntityId] = stateFrom;

                // 3. Perform Linear Interpolation for all dynamic entities
                foreach (var stateTo in to.States)
                {
                    if (stateTo.EntityId == localPlayerId) continue; // Skip ourselves (handled by CSP)
                    moved = true;

                    if (_interpolationFrom.TryGetValue(stateTo.EntityId, out var stateFrom) && stateFrom.EntityId != 0)
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

        // Only a frame that actually moved something invalidates the snapshot. A frame that found no
        // bracketing pair changed nothing, and rebuilding a copy of an unchanged world is the exact cost
        // this cache exists to remove.
        if (moved) Touch();
    }

    public void SyncState(IEnumerable<EntityState> states)
    {
        foreach (var s in states)
        {
            _serverTransforms[s.EntityId] = s.Transform.ToTransform();
            _serverVelocities[s.EntityId] = s.LinearVelocity;
        }
        Touch();
    }

    /// <summary>
    /// The world as the simulation and audio threads read it: one immutable copy per version of the world.
    ///
    /// Everything that consumes a snapshot within a frame — prediction, the shelter raycast, the proximity
    /// scan, the audio system, the acoustic worker — now shares the same object rather than each paying to
    /// rebuild it. The copy is never mutated after it is built, which is what makes sharing it across
    /// threads safe; a change to the world produces a NEW copy, it never edits the one already handed out.
    /// </summary>
    public WorldSnapshot GetSnapshot()
    {
        long version = Interlocked.Read(ref _version);

        var cached = _cached;
        if (cached != null && cached.Version == version) return cached.Snapshot;

        lock (_snapshotLock)
        {
            // Re-read under the lock: another thread may have built exactly this version while we waited.
            version = Interlocked.Read(ref _version);
            cached = _cached;
            if (cached != null && cached.Version == version) return cached.Snapshot;

            var built = BuildSnapshot();
            // Stamped with the version read BEFORE the build. A mutation that lands mid-build leaves
            // _version ahead of the stamp, so the next caller rebuilds — stale data is never labelled fresh.
            _cached = new CachedSnapshot(version, built);
            Interlocked.Increment(ref _snapshotBuilds);
            PerfProbe.Count("client.snapshot.build");
            return built;
        }
    }

    private WorldSnapshot BuildSnapshot()
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

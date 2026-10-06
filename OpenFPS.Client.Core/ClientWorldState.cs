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

    /// <summary>The interpolated transform of one entity, if the client has one. Diagnostic and test
    /// access: the snapshot only carries entities whose definition has arrived, and whether the
    /// INTERPOLATOR is still moving things is a separate question from that.</summary>
    public bool TryGetInterpolatedTransform(int entityId, out Transform transform)
        => _serverTransforms.TryGetValue(entityId, out transform);
    private readonly ConcurrentDictionary<int, Vector3> _serverVelocities = new();
    private readonly ConcurrentDictionary<int, float> _serverTyreDemand = new();
    /// <summary>Each vehicle's wheels, as last sent. Not interpolated: a wheel's load and slip are
    /// read for sound, which smooths what it reads.</summary>
    private readonly ConcurrentDictionary<int, WheelState[]> _serverWheels = new();
    private readonly ConcurrentDictionary<int, byte> _audioEntityIds = new();
    /// <summary>Everything that declares a region, so the moved-region check is not a walk over the
    /// whole map every frame. See WorldSnapshot.RegionEntityIds.</summary>
    private readonly ConcurrentDictionary<int, byte> _regionEntityIds = new();
    /// <summary>Fixed beacons that are not solid, such as stair markers, and named places. See IsMarker.</summary>
    private readonly ConcurrentDictionary<int, byte> _markerEntityIds = new();

    // --- Snapshot Interpolation ---
    private readonly List<ServerStateUpdate> _snapshotBuffer = new();
    private const double InterpolationDelay = 0.1; // 100ms buffer

    /// <summary>
    /// How many server snapshots of history to keep.
    ///
    /// Ten was 333 ms at the 30 Hz tick, and playback sits 100 ms behind the newest — so there were
    /// 233 ms of margin before the snapshot the interpolator is reading FROM got pruned out from
    /// under it. When that happens no bracketing pair exists and every entity in the world simply
    /// holds its position until one does, with no correction and nothing in the log. Heard from the
    /// grandstand as some of the cars stopping in front of you for about a second and then carrying
    /// on — "some" because a frozen car straight in front changes bearing enormously and a frozen
    /// one on the far side of the track does not.
    ///
    /// Thirty is a full second of history. It is a list of references; the cost is nothing.
    /// </summary>
    private const int SnapshotHistory = 30;

    /// <summary>Past this far out of step, the playback clock is snapped rather than eased — the
    /// stream stopped and restarted, and one jump beats seconds of a world that does not move.</summary>
    private const double MaxDriftBeforeSnap = 0.5;

    /// <summary>Times the playback clock has run outside the buffer. Diagnostic — it should be 0.</summary>
    public int InterpolationStalls => _interpolationStalls;
    private int _interpolationStalls;

    /// <summary>When the interpolated transforms were last advanced, seconds on
    /// <see cref="OpenFPS.Common.AudioClock"/>. Copied into every snapshot built from them.</summary>
    private double _positionsSampledAt;
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

    /// <summary>Each moving thing's latest state at or before the snapshot playback is reading from, and
    /// that snapshot's tick: what it is doing while the server is not mentioning it. See HoldThrough.
    /// Guarded by the snapshot buffer's lock.</summary>
    private readonly Dictionary<int, (long Tick, EntityState State)> _held = new();
    private long _heldThrough = long.MinValue;
    private readonly List<int> _folded = new();
    private readonly HashSet<int> _inTo = new();

    /// <summary>Below this (m/s, squared) a held state is resting: the server only stops sending a thing
    /// whose velocity is zero on the wire, which is under a millimetre a second.</summary>
    private const float RestingSpeedSquared = 1e-6f;
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
        // The wind every tree, fire and ear on the map reads: this broadcast, reached from the last one
        // over a second so it never lands as a step (WindWeather). The session hands it to WindField;
        // kept here rather than written there, so a world built in a test does not blow on every
        // other test's trees.
        var air = WindAir.FromBroadcast(update.WindVelocity, update.WindGustiness,
                                        update.WindClock, update.WindTravelEast, update.WindTravelNorth);
        Wind = Wind.Following(air, WindField.Now());
        Touch();
    }

    /// <summary>The server's wind as this client follows it (see <see cref="UpdateAtmosphere"/>).</summary>
    public WindWeather Wind
    {
        get => Volatile.Read(ref _wind);
        private set => Volatile.Write(ref _wind, value);
    }
    private WindWeather _wind = WindWeather.Default;

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

    public void Clear(Vector3 size)
    {
        _definitions.Clear();
        _serverTransforms.Clear();
        _serverVelocities.Clear();
        _serverTyreDemand.Clear();
        _serverWheels.Clear();
        _audioEntityIds.Clear();
        _regionEntityIds.Clear();
        _markerEntityIds.Clear();
        lock (_snapshotBuffer) _held.Clear();

        lock (_metaLock)
        {
            CurrentMapSize = size;
        }

        lock (_gridLock)
        {
            _staticGrid = new SpatialGrid<int>(10.0f);
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

        // A room that turned up after the map was baked — a building somebody put down while we were
        // standing here, or the inside of a car, which is never in the bake at all because it moves.
        // Without this the acoustic map never hears of it and stepping inside sounds like stepping
        // nowhere.
        if (def.Region.RoomSize.X > 0f)
        {
            TrackRegion(def);
            _regionEntityIds[def.EntityId] = 0;
        }

        // A door's aperture is not a fixed property of it, it is how far the leaf has swung. The
        // server re-sends the definition as it moves, and this is what turns that into the opening
        // the acoustics actually use — without it a door swings silently and nothing sounds different
        // on the other side of it, which is the entire point of a door.
        if (def.Portal.RegionAId != def.Portal.RegionBId) TrackPortal(def);

        // Anything that makes sound on its own is processed every frame. See RunsOnItsOwn: this used
        // to be a list of two playback modes rather than a rule, and everything outside the list was
        // silently absent from the audio system entirely.
        if (def.SoundEmitter.RunsOnItsOwn())
            _audioEntityIds[def.EntityId] = 0;

        if (IsMarker(def)) _markerEntityIds[def.EntityId] = 0;
        else _markerEntityIds.TryRemove(def.EntityId, out _);

        lock (_gridLock)
        {
            _gridNeedsRebuild = true;
        }

        Touch();
    }

    /// <summary>
    /// Puts a runtime region on the acoustic map, or updates one already there.
    ///
    /// Build-then-swap rather than mutating in place, because the region tables are read without a
    /// lock from the audio worker and the FMOD thread while this runs on the network one, and adding
    /// a key to a dictionary somebody else is enumerating throws. The tables are small and this
    /// happens when a building arrives, not per frame, so a copy costs nothing worth having.
    ///
    /// Deliberately NOT voxelized. The voxel grid is the fallback for entities the client cannot see
    /// in its snapshot, and a composite's room is an entity it can always see; the exact
    /// point-in-box test against the live transform is both cheaper and right, and it is the only one
    /// that can be right for a room that moves.
    /// </summary>
    private void TrackRegion(EntityDefinition def)
    {
        lock (_metaLock)
        {
            var map = AcousticMap;
            if (map == null) return;   // still loading; the bake will pick it up from the stream

            map.Regions = new Dictionary<int, RegionComponent>(map.Regions) { [def.EntityId] = def.Region };
            map.RegionPositions = new Dictionary<int, Vector3>(map.RegionPositions) { [def.EntityId] = def.Transform.Position };
            map.RegionRotations = new Dictionary<int, Quaternion>(map.RegionRotations) { [def.EntityId] = def.Transform.Rotation };

            // Its open sides are openings to whatever is beyond them, as a map room's are (the bake does
            // the same for every room it was given). Not for a room that moves: an opening is a fixed
            // place on the map, and the inside of a car would leave its windows behind at the kerb.
            if (!def.Moves)
                OpenFPS.Common.Systems.AcousticVolumeGenerator.AddFaceOpenings(map, _definitions.Values, new[] { def.EntityId });
        }
    }

    /// <summary>
    /// Puts a portal on the acoustic map, or moves the one already there.
    ///
    /// A shut door has no aperture and is therefore not an opening at all, so it comes straight back
    /// off — which is right, and is the same thing the map bake does with an aperture of zero.
    ///
    /// Build-then-swap for the same reason as the regions: these tables are walked without a lock
    /// from the audio worker while this runs on the network thread.
    /// </summary>
    private void TrackPortal(EntityDefinition def)
    {
        lock (_metaLock)
        {
            var map = AcousticMap;
            if (map == null) return;

            bool openable = def.Portal.ApertureSize > 0f;
            bool known = map.Portals.ContainsKey(def.EntityId);
            if (!openable && !known) return;

            var portals = new Dictionary<int, (PortalComponent Portal, Vector3 Position)>(map.Portals);
            if (openable) portals[def.EntityId] = (def.Portal, def.Transform.Position);
            else portals.Remove(def.EntityId);
            map.Portals = portals;
        }
    }

    /// <summary>Takes a region off the acoustic map when whatever enclosed it is gone.</summary>
    private void ForgetRegions(List<int> entityIds)
    {
        lock (_metaLock)
        {
            var map = AcousticMap;
            if (map == null) return;

            List<int>? present = null;
            foreach (int id in entityIds)
                if (map.Regions.ContainsKey(id)) (present ??= new List<int>()).Add(id);
            if (present == null) return;

            var regions = new Dictionary<int, RegionComponent>(map.Regions);
            var positions = new Dictionary<int, Vector3>(map.RegionPositions);
            var rotations = new Dictionary<int, Quaternion>(map.RegionRotations);
            foreach (int id in present) { regions.Remove(id); positions.Remove(id); rotations.Remove(id); }
            map.Regions = regions;
            map.RegionPositions = positions;
            map.RegionRotations = rotations;

            // ...and the openings in its faces go with it.
            List<int>? openings = null;
            foreach (var (id, frame) in map.OpeningFrames)
                if (present.Contains(frame.Room)) (openings ??= new List<int>()).Add(id);
            if (openings != null)
            {
                var portals = new Dictionary<int, (PortalComponent Portal, Vector3 Position)>(map.Portals);
                var frames = new Dictionary<int, OpeningFrame>(map.OpeningFrames);
                foreach (int id in openings) { portals.Remove(id); frames.Remove(id); }
                map.Portals = portals;
                map.OpeningFrames = frames;
            }
        }
    }

    /// <summary>...and the same for a doorway whose door is gone.</summary>
    private void ForgetPortals(List<int> entityIds)
    {
        lock (_metaLock)
        {
            var map = AcousticMap;
            if (map == null) return;

            List<int>? present = null;
            foreach (int id in entityIds)
                if (map.Portals.ContainsKey(id)) (present ??= new List<int>()).Add(id);
            if (present == null) return;

            var portals = new Dictionary<int, (PortalComponent Portal, Vector3 Position)>(map.Portals);
            foreach (int id in present) portals.Remove(id);
            map.Portals = portals;
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
            _serverTyreDemand.TryRemove(id, out _);
            _serverWheels.TryRemove(id, out _);
            _audioEntityIds.TryRemove(id, out _);
            _regionEntityIds.TryRemove(id, out _);
            _markerEntityIds.TryRemove(id, out _);
            lock (_snapshotBuffer) _held.Remove(id);
            if (known) removed.Add(id);
        }

        if (removed.Count > 0)
        {
            ForgetRegions(removed);
            ForgetPortals(removed);
            lock (_gridLock)
            {
                _gridNeedsRebuild = true;
            }
            Touch();
        }
        return removed;
    }

    /// <summary>
    /// Takes one world-state packet into the interpolation buffer, MERGING it with any packet
    /// already held for the same tick.
    ///
    /// A tick's world state is not always one packet. LiteNetLib will not fragment an unreliable
    /// send, so past the peer's limit the server splits a tick across several packets that all carry
    /// the same Tick — see NetworkService.SendStateUpdate. Filing those as separate snapshots would
    /// be worse than the problem it solves: the interpolator brackets the playback time between two
    /// buffered snapshots and divides by the time between them, so two entries with the SAME tick is
    /// a zero denominator, and each of them holds only half the world anyway.
    ///
    /// Merging by entity id rather than appending, so a retransmitted or duplicated state replaces
    /// rather than doubling.
    /// </summary>
    public void SyncState(ServerStateUpdate update)
    {
        lock (_snapshotBuffer)
        {
            ServerStateUpdate? existing = null;
            for (int i = _snapshotBuffer.Count - 1; i >= 0; i--)
                if (_snapshotBuffer[i].Tick == update.Tick) { existing = _snapshotBuffer[i]; break; }

            if (existing != null)
            {
                foreach (var st in update.States)
                {
                    int at = existing.States.FindIndex(e => e.EntityId == st.EntityId);
                    if (at >= 0) existing.States[at] = st;
                    else existing.States.Add(st);
                }
                existing.LastProcessedSequenceId = Math.Max(existing.LastProcessedSequenceId, update.LastProcessedSequenceId);
                return;
            }

            _snapshotBuffer.Add(update);
            while (_snapshotBuffer.Count > SnapshotHistory) _snapshotBuffer.RemoveAt(0); // Prune old history
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
            double oldestServerTime = _snapshotBuffer[0].Tick * PhysicsConstants.FixedDeltaTime;
            double target = latestServerTime - InterpolationDelay;
            if (_clientInterpolationTime == 0) _clientInterpolationTime = target;

            // ── Keep the playback clock ON the server's clock ────────────────────────────────
            //
            // It used to be set once and then advanced by the CLIENT's own dt for ever, which makes
            // it an independent clock: two crystals, two frame-rate regimes, no correction anywhere.
            // They drift, and when the drift exceeds the buffer the bracket search below finds no
            // pair, every entity freezes where it stands, and nothing says so. Minutes in, that is
            // what "some of the cars stop in front of me, then keep going" was.
            //
            // Corrected by RATE, not by jumping: playback runs up to 10 % fast or slow to close the
            // gap. Setting the time directly would move every entity in the world at once, which is
            // the very artefact this is here to avoid. A gap too big for that to fix in reasonable
            // time is not drift, it is a stall — a stream that stopped and restarted — and there one
            // jump now beats several seconds of a frozen world.
            double error = target - _clientInterpolationTime;
            if (Math.Abs(error) > MaxDriftBeforeSnap)
            {
                _clientInterpolationTime = target;
                _interpolationStalls++;
                Serilog.Log.Debug("Interpolation clock resynchronised: {Error:F2} s out, {Count} snapshot(s) buffered.",
                                  error, _snapshotBuffer.Count);
            }
            else
            {
                _clientInterpolationTime += dt * Math.Clamp(1.0 + error * 2.0, 0.9, 1.1);
            }

            // And never outside what the buffer can actually serve, whatever the arithmetic above did.
            if (_clientInterpolationTime > latestServerTime) _clientInterpolationTime = latestServerTime;
            if (_clientInterpolationTime < oldestServerTime) _clientInterpolationTime = oldestServerTime;

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

                if (from.Tick != _heldThrough) moved |= HoldThrough(from, to, localPlayerId);

                // 3. Perform Linear Interpolation for all dynamic entities
                foreach (var stateTo in to.States)
                {
                    if (stateTo.EntityId == localPlayerId) continue; // Skip ourselves (handled by CSP)
                    moved = true;

                    // Where it was at 'from'. Not in that snapshot is not the same as unknown: the server
                    // leaves out anything that has not moved (RestingStates), so a car pulling away from
                    // the kerb is in 'to' and was last mentioned some ticks ago, resting where it still
                    // was at 'from'. Snapping it to 'to' would move it a tick early, every time anything
                    // set off.
                    double fromTime = t0;
                    bool haveFrom = _interpolationFrom.TryGetValue(stateTo.EntityId, out var stateFrom) && stateFrom.EntityId != 0;
                    if (!haveFrom && _held.TryGetValue(stateTo.EntityId, out var held))
                    {
                        stateFrom = held.State;
                        haveFrom = true;
                        // Something that was moving when it was last heard of, though, is missing from
                        // 'from' because that packet was lost, not because it stopped: it has been
                        // travelling since, so it goes from where it was then.
                        if (held.State.LinearVelocity.LengthSquared() > RestingSpeedSquared)
                            fromTime = held.Tick * PhysicsConstants.FixedDeltaTime;
                    }

                    if (haveFrom)
                    {
                        float a = fromTime == t0 ? alpha : (float)((_clientInterpolationTime - fromTime) / (t1 - fromTime));
                        var transFrom = stateFrom.Transform.ToTransform();
                        var transTo = stateTo.Transform.ToTransform();

                        var lerpedPos = Vector3.Lerp(transFrom.Position, transTo.Position, a);
                        var lerpedRot = Quaternion.Slerp(transFrom.Rotation, transTo.Rotation, a);

                        _serverTransforms[stateTo.EntityId] = new Transform { Position = lerpedPos, Rotation = lerpedRot };
                        _serverVelocities[stateTo.EntityId] = Vector3.Lerp(stateFrom.LinearVelocity, stateTo.LinearVelocity, a);
                        _serverTyreDemand[stateTo.EntityId] = stateTo.TyreDemandFraction;
                        if (stateTo.Wheels != null) _serverWheels[stateTo.EntityId] = stateTo.Wheels;
                    }
                    else
                    {
                        // Never heard of before: it starts where it is.
                        _serverTransforms[stateTo.EntityId] = stateTo.Transform.ToTransform();
                        _serverVelocities[stateTo.EntityId] = stateTo.LinearVelocity;
                        _serverTyreDemand[stateTo.EntityId] = stateTo.TyreDemandFraction;
                        if (stateTo.Wheels != null) _serverWheels[stateTo.EntityId] = stateTo.Wheels;
                    }
                }
            }
        }

        // Only a frame that actually moved something invalidates the snapshot. A frame that found no
        // bracketing pair changed nothing, and rebuilding a copy of an unchanged world is the exact cost
        // this cache exists to remove.
        //
        // The stamp goes on at the same moment, and only when something moved, because it is an answer
        // to "how old is this position" and a frame that produced no new position did not make the old
        // one any younger. Everything downstream — dead reckoning most of all — measures from here.
        if (moved)
        {
            _positionsSampledAt = OpenFPS.Common.AudioClock.Now;
            Touch();
        }
    }

    /// <summary>
    /// Brings the held states up to the 'from' snapshot, and puts down exactly where it rests anything
    /// that is not in 'to'. Run once each time playback moves into a new pair of snapshots.
    ///
    /// The server sends a moving thing every tick and a resting one hardly at all: a few repeats as it
    /// stops, then once a second (RestingStates). So a snapshot no longer lists the whole world, and
    /// what a snapshot does not mention has to stay where it was last put — not freeze wherever the
    /// interpolation happened to have got to between two ticks, a few centimetres short of where it
    /// stopped. Every snapshot played through is folded in, including any a slow frame skipped over, so
    /// a resting state is never missed because the frame that would have read it never came.
    /// </summary>
    private bool HoldThrough(ServerStateUpdate from, ServerStateUpdate to, int localPlayerId)
    {
        // Playback went backwards (the clock was resynchronised): fold again from the start of what is held.
        if (from.Tick < _heldThrough) _heldThrough = long.MinValue;

        _folded.Clear();
        foreach (var snapshot in _snapshotBuffer)
        {
            if (snapshot.Tick <= _heldThrough) continue;
            if (snapshot.Tick > from.Tick) break;
            foreach (var s in snapshot.States)
            {
                _held[s.EntityId] = (snapshot.Tick, s);
                _folded.Add(s.EntityId);
                // Wheels are not interpolated; a state without them means "as they were".
                if (s.Wheels != null && s.EntityId != localPlayerId) _serverWheels[s.EntityId] = s.Wheels;
            }
        }
        _heldThrough = from.Tick;

        _inTo.Clear();
        foreach (var s in to.States) _inTo.Add(s.EntityId);
        bool any = false;
        foreach (int id in _folded)
        {
            if (id == localPlayerId || _inTo.Contains(id)) continue;
            var state = _held[id].State;
            _serverTransforms[id] = state.Transform.ToTransform();
            _serverVelocities[id] = state.LinearVelocity;
            _serverTyreDemand[id] = state.TyreDemandFraction;
            any = true;
        }
        return any;
    }

    public void SyncState(IEnumerable<EntityState> states)
    {
        foreach (var s in states)
        {
            _serverTransforms[s.EntityId] = s.Transform.ToTransform();
            _serverVelocities[s.EntityId] = s.LinearVelocity;
            _serverTyreDemand[s.EntityId] = s.TyreDemandFraction;
            if (s.Wheels != null) _serverWheels[s.EntityId] = s.Wheels;
        }
        _positionsSampledAt = OpenFPS.Common.AudioClock.Now;
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
            AcousticMap = AcousticMap,
            PositionsSampledAt = _positionsSampledAt
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
                Velocity = _serverVelocities.GetValueOrDefault(id, Vector3.Zero),
                TyreDemand = _serverTyreDemand.GetValueOrDefault(id, 0f),
                Wheels = _serverWheels.GetValueOrDefault(id),
            };
            snap.Entities[id] = s;
            if (s.Definition.Type != EntityType.StaticObject || s.Definition.Moves) snap.DynamicEntities.Add(s);
        }

        snap.AudioEntityIds.AddRange(_audioEntityIds.Keys);
        snap.RegionEntityIds.AddRange(_regionEntityIds.Keys);
        snap.MarkerEntityIds.AddRange(_markerEntityIds.Keys);
        return snap;
    }

    /// <summary>A fixed thing nothing can bump into, in neither the static grid nor the moving things,
    /// that something looks for near you: a beacon such as a stair marker, or a named place such as a
    /// flight or a landing (<see cref="NamedPlaces"/>). Each consumer takes its own kind from the list.</summary>
    internal static bool IsMarker(EntityDefinition def)
        => def.Type == EntityType.StaticObject && !def.Moves && !def.Collider.IsSolid
           && (!string.IsNullOrEmpty(def.Identity.BeaconCategory) || NamedPlaces.Is(def));

    private void RebuildGrid()
    {
        if (_staticGrid == null) return;
        _staticGrid.ClearAll(); // Clear both static and dynamic just in case, though it's the static grid.
        foreach (var kvp in _definitions)
        {
            if (kvp.Value.Type == EntityType.StaticObject && !kvp.Value.Moves && kvp.Value.Collider.IsSolid)
            {
                _staticGrid.AddOverlapping(kvp.Value.Transform.Position, kvp.Value.Collider.Size, kvp.Value.Transform.Rotation, kvp.Key, isStatic: true);
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

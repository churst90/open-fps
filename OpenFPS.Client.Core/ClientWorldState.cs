using System.Numerics;
using System.Collections.Concurrent;
using OpenFPS.Common.Components;
using OpenFPS.Common.Networking;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// The client's copy of the world: definitions, interpolated transforms, weather and the acoustic map,
/// from the network messages, served as immutable snapshots.
/// </summary>
public class ClientWorldState
{
    private readonly ConcurrentDictionary<int, EntityDefinition> _definitions = new();
    private readonly ConcurrentDictionary<int, Transform> _serverTransforms = new();

    /// <summary>The interpolated transform of one entity, whether or not its definition has arrived.
    /// Diagnostic and test access.</summary>
    public bool TryGetInterpolatedTransform(int entityId, out Transform transform)
        => _serverTransforms.TryGetValue(entityId, out transform);

    /// <summary>...with its velocity as the audio is given it. Diagnostic and test access.</summary>
    public bool TryGetInterpolatedMotion(int entityId, out Transform transform, out Vector3 velocity)
    {
        velocity = _serverVelocities.GetValueOrDefault(entityId);
        return _serverTransforms.TryGetValue(entityId, out transform);
    }

    /// <summary>Every entity that has an interpolated transform. Diagnostic and test access.</summary>
    public ICollection<int> InterpolatedIds() => _serverTransforms.Keys;
    private readonly ConcurrentDictionary<int, Vector3> _serverVelocities = new();
    private readonly ConcurrentDictionary<int, float> _serverTyreDemand = new();
    /// <summary>Each vehicle's horn and siren switches as last sent (EntityState.Signals).</summary>
    private readonly ConcurrentDictionary<int, byte> _serverSignals = new();
    /// <summary>Each vehicle's wheels, as last sent. Not interpolated: a wheel's load and slip are
    /// read for sound, which smooths what it reads.</summary>
    private readonly ConcurrentDictionary<int, WheelState[]> _serverWheels = new();
    private readonly ConcurrentDictionary<int, byte> _audioEntityIds = new();
    /// <summary>Everything that declares a region, so the moved-region check is not a walk over the
    /// whole map every frame. See WorldSnapshot.RegionEntityIds.</summary>
    private readonly ConcurrentDictionary<int, byte> _regionEntityIds = new();
    /// <summary>Fixed beacons that are not solid, such as stair markers, and named places. See IsMarker.</summary>
    private readonly ConcurrentDictionary<int, byte> _markerEntityIds = new();

    private readonly List<ServerStateUpdate> _snapshotBuffer = new();
    private const double InterpolationDelay = 0.1;

    /// <summary>
    /// Server snapshots of history kept: a second at the 30 Hz tick. Ten (333 ms) left 233 ms before
    /// the snapshot being read from was pruned, and then every entity held its place until a bracketing
    /// pair returned, silently: heard from the grandstand as cars stopping for a second and carrying on.
    /// </summary>
    private const int SnapshotHistory = 30;

    /// <summary>Past this far out of step, the playback clock is snapped rather than eased — the
    /// stream stopped and restarted, and one jump beats seconds of a world that does not move.</summary>
    private const double MaxDriftBeforeSnap = 0.5;

    /// <summary>When the interpolated transforms were last advanced, seconds on
    /// <see cref="OpenFPS.Common.AudioClock"/>. Copied into every snapshot built from them.</summary>
    private double _positionsSampledAt;
    private double _clientInterpolationTime = 0;

    /// <summary>The server time, seconds, the interpolation is playing. Diagnostic and test access.</summary>
    public double PlaybackTime => _clientInterpolationTime;
    
    private readonly object _gridLock = new();
    private SpatialGrid<int>? _staticGrid;

    /// <summary>The static solids as a triangle world (docs/GEOMETRY.md stage 1), built off the game
    /// thread and handed to every snapshot.</summary>
    public ClientGeometry Geometry { get; } = new(250f);

    /// <summary>The mesh assets the definitions name (docs/GEOMETRY.md 4.4).</summary>
    public MeshAssetFetcher Meshes { get; } = new();

    /// <summary>Mesh assets arrived: the things made of them are built again with their own shapes.</summary>
    public void MeshesArrived(IEnumerable<int> entityIds)
    {
        foreach (int id in entityIds) Geometry.Invalidate(id);
        Touch();
    }
    private bool _geometryHooked;
    private bool _gridNeedsRebuild = false;

    private readonly object _metaLock = new();
    public AcousticMap? AcousticMap { get; private set; }
    public Vector3 CurrentMapSize { get; private set; } = new Vector3(200, 100, 200);

    private readonly object _envLock = new();
    private WorldEnvironmentComponent _env = new();

    // A snapshot is a full copy of the world and read-only once built, so one is built per version of the
    // world (every mutation bumps _version) and shared by everything that asks in a frame. Version and copy
    // are one immutable object, so a reader never sees a new version stamped on an old copy.
    private sealed record CachedSnapshot(long Version, WorldSnapshot Snapshot);

    /// <summary>Each moving thing's latest state at or before the snapshot playback is reading from, that
    /// snapshot's tick, and how it was changing as the server sent it (DistantMotion.Rates): what it is
    /// doing while the server is not mentioning it. See HoldThrough. Guarded by the snapshot buffer's lock.</summary>
    private readonly Dictionary<int, Held> _held = new();
    private long _heldThrough = long.MinValue;

    private struct Held
    {
        public long Tick;
        public EntityState State;
        public DistantMotion.Rates Rates;
    }

    /// <summary>Each thing's next state after the playback point, this frame: the first snapshot from 'to'
    /// on that mentions it. A far thing is mentioned a few times a second (DistantMotion).</summary>
    private readonly Dictionary<int, (long Tick, EntityState State)> _next = new();

    /// <summary>Ticks the buffer has nothing for, this frame, in order: lost, or not here yet
    /// (<see cref="FirstMissingAfter"/>).</summary>
    private readonly List<long> _missing = new();

    /// <summary>Things resting where they were put, with nothing newer on the way: not placed again until
    /// something about them arrives.</summary>
    private readonly HashSet<int> _settled = new();

    /// <summary>What each moving thing was carried by last frame, and what is left of a correction being
    /// eased out (<see cref="BlendSeconds"/>).</summary>
    private readonly Dictionary<int, Carried> _carried = new();

    private struct Carried
    {
        public Held A;
        public long BTick;
        public EntityState B;
        /// <summary>The playback time B was first seen at.</summary>
        public double Learned;
        /// <summary>The first tick after A the buffer had nothing for.</summary>
        public long Missing;
        /// <summary>The playback time it was last placed at.</summary>
        public double Time;
        public Vector3 OffsetPosition, OffsetVelocity;
        public Quaternion OffsetRotation;
        public bool Offset;
    }

    /// <summary>
    /// How long a correction takes to ease out, seconds (a time constant). A far thing's next state can
    /// arrive late, or after one was lost, when the client has already carried the thing past where that
    /// state would start it from; the difference is eased away rather than jumped.
    /// </summary>
    private const float BlendSeconds = 0.1f;

    /// <summary>...and a correction to the velocity, which the pitch follows: a tick.</summary>
    private const float VelocityBlendSeconds = PhysicsConstants.FixedDeltaTime;

    /// <summary>A correction bigger than this is a jump (a teleport, a resynchronised clock), not an error to
    /// ease: it is taken at once, as before.</summary>
    private const float MaxBlendMetres = 5f;

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
    /// How many entities the client knows about, without building a snapshot: map load reports progress
    /// per definition, and a snapshot per definition made the load quadratic.
    /// </summary>
    public int EntityCount => _definitions.Count;

    /// <summary>Marks the world changed, so the next <see cref="GetSnapshot"/> rebuilds.</summary>
    private void Touch() => Interlocked.Increment(ref _version);

    public float CurrentPrecipitation { get { lock(_envLock) return _env.PrecipitationIntensity; } }
    /// <summary>The rain rate, mm/h, as the server worked it out.</summary>
    public float CurrentRainRate { get { lock(_envLock) return _rainRate; } }
    private float _rainRate;
    private Precipitation _precipitation = Precipitation.None;
    /// <summary>The water on this map's roads as the server last sent it: dry until one is sent.</summary>
    private readonly RoadWater _roadWater = new();
    /// <summary>The water in a wheel path of an asphalt road (2.5 m from the crown, no kerb, no puddle),
    /// mm: for a vehicle whose wheels the server does not send.</summary>
    private float _roadWaterMm;

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
            _rainRate = update.RainRateMmPerHour;
            _roadWater.Load(update.RoadWater);
            _roadWaterMm = _roadWater.WaterMm(RoadSurfaces.IndexOf(RoadData.DefaultSurface), 2.5f, float.PositiveInfinity, 0f);
            _precipitation = new Precipitation((PrecipitationKind)Math.Clamp(update.PrecipitationKind, 0, 4),
                                               update.RainRateMmPerHour, update.RainMedianDropMm, update.HailDiameterMm);
        }
        // Reached from the last broadcast over a second, never a step (WindWeather). The session hands it
        // to WindField: kept here, so a world built in a test does not blow on every other test's trees.
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
    /// Applies the map's authored atmosphere the moment the manifest lands, so the first second (before
    /// the next <see cref="UpdateAtmosphere"/>) is not heard through the previous map's air.
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

    // Nonsense from a server (an old build, a map in atmospheres, an unset field) gets the neutral value,
    // not a zero in a divisor that switches the acoustics off.
    private static float SanePressure(float mb) => mb is > 300f and < 1100f ? mb : 1013.25f;
    private static float SaneAbsorptionMultiplier(float m) => m > 0.01f ? m : 1.0f;

    public void Clear(Vector3 size)
    {
        _definitions.Clear();
        _serverTransforms.Clear();
        _serverVelocities.Clear();
        _serverTyreDemand.Clear();
        _serverSignals.Clear();
        _serverWheels.Clear();
        _audioEntityIds.Clear();
        _regionEntityIds.Clear();
        _markerEntityIds.Clear();
        lock (_snapshotBuffer) { _held.Clear(); _carried.Clear(); _settled.Clear(); }

        lock (_metaLock)
        {
            CurrentMapSize = size;
            // A refresh still running for the last map is thrown away when it finishes (RefreshAcousticsNow).
            _mapEpoch++;
        }
        _tiles.Clear();
        _woods = null;
        _crownIds.Clear();
        _crownsDirty = false;
        _woodIds.Clear();
        _woodKeys.Clear();

        lock (_gridLock)
        {
            _staticGrid = new SpatialGrid<int>(10.0f);
            _gridNeedsRebuild = true;
        }
        Geometry.Reset(TileMetres);

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

    /// <summary>
    /// A definition arrived or changed. With <paramref name="deferAcoustics"/> (a tile of a streamed map)
    /// its rooms and doorways wait for the tile's acoustic rebuild (<see cref="RequestAcousticRefresh"/>):
    /// one at a time, each room copied every region table and surveyed its walls on the game thread, and a
    /// room that came before its walls was found open on every side.
    /// </summary>
    public void RegisterDefinition(EntityDefinition def, bool deferAcoustics = false)
    {
        _definitions.TryGetValue(def.EntityId, out var before);
        _definitions[def.EntityId] = def;
        _serverTransforms[def.EntityId] = def.Transform;
        if (def.Collider.Form is { Kind: OpenFPS.Common.Geometry.ShapeKind.Mesh } meshForm) Meshes.Want(meshForm.Mesh, def.EntityId);
        Geometry.Note(before, def);

        // A room that arrived after the map was baked: a building put down in play, or a car's inside,
        // never in the bake because it moves.
        if (def.Region.RoomSize.X > 0f)
        {
            if (!deferAcoustics) TrackRegion(def);
            else _tablesDirty = true;
            _regionEntityIds[def.EntityId] = 0;
        }

        // A door's aperture is how far its leaf has swung; the server re-sends the definition as it moves,
        // and this turns it into the opening the acoustics use.
        if (def.Portal.RegionAId != def.Portal.RegionBId)
        {
            if (!deferAcoustics) TrackPortal(def);
            // A front door in a coarse tile, its room not sent: a shut leaf in a wall, nothing for the
            // tables (the refresh leaves its doorway out). Only a doorway whose rooms are here counts.
            else if (Holds(def.Portal.RegionAId) && Holds(def.Portal.RegionBId)) _tablesDirty = true;
        }

        // Anything that makes sound on its own is processed every frame (RunsOnItsOwn is the rule; a list
        // of two playback modes once left everything else silent).
        if (def.SoundEmitter.RunsOnItsOwn())
            _audioEntityIds[def.EntityId] = 0;

        if (!IsWood(def.EntityId) && def.SoundEmitter.IsSynth
            && def.SoundEmitter.SoundId?.StartsWith("foliage:", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (_crownIds.TryAdd(def.EntityId, 0)) _crownsDirty = true;
        }
        if (IsMarker(def)) _markerEntityIds[def.EntityId] = 0;
        else _markerEntityIds.TryRemove(def.EntityId, out _);

        lock (_gridLock)
        {
            _gridNeedsRebuild = true;
        }

        Touch();
    }

    /// <summary>Whether a doorway's side is somewhere this client has: the outdoors, or a room it holds.</summary>
    private bool Holds(int region) => region == AcousticConstants.GlobalRegionId || _definitions.ContainsKey(region);

    /// <summary>
    /// Puts a runtime region on the acoustic map, or updates one already there.
    ///
    /// <para>Build-then-swap: the region tables are read without a lock by the audio worker and the FMOD
    /// thread while this runs on the network thread, and adding a key to a dictionary being enumerated
    /// throws.</para>
    ///
    /// <para>Not voxelized: the voxel grid is for regions with no entity in the snapshot, and the exact
    /// point-in-box test is cheaper and the only one right for a room that moves.</para>
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

            // Its open sides are openings, as the bake makes a map room's. Not for a room that moves: an
            // opening is a fixed place, and a car would leave its windows behind at the kerb.
            if (!def.Moves)
                OpenFPS.Common.Systems.AcousticVolumeGenerator.AddFaceOpenings(map, _definitions.Values, new[] { def.EntityId });
        }
    }

    /// <summary>
    /// Puts a portal on the acoustic map, or moves the one already there. A shut door (aperture zero) is
    /// not an opening and comes off, as in the map bake. Build-then-swap, as for the regions.
    /// </summary>
    private void TrackPortal(EntityDefinition def)
    {
        lock (_metaLock)
        {
            var map = AcousticMap;
            if (map == null) return;

            // On a streamed map a front door can be held without its room (a coarse tile has the shell
            // and the door, not the rooms): a shut leaf in a wall, and no doorway into nothing.
            bool openable = def.Portal.ApertureSize > 0f
                            && (TileMetres <= 0f || (Holds(def.Portal.RegionAId) && Holds(def.Portal.RegionBId)));
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

            // The voxel grid still has them: the next refresh makes it again.
            _tablesDirty = true;
            var regions = new Dictionary<int, RegionComponent>(map.Regions);
            var positions = new Dictionary<int, Vector3>(map.RegionPositions);
            var rotations = new Dictionary<int, Quaternion>(map.RegionRotations);
            foreach (int id in present) { regions.Remove(id); positions.Remove(id); rotations.Remove(id); }
            map.Regions = regions;
            map.RegionPositions = positions;
            map.RegionRotations = rotations;

            // ...and the openings in its faces go with it.
            List<int>? openings = null;
            var gone = new HashSet<int>(present);
            foreach (var (id, frame) in map.OpeningFrames)
                if (gone.Contains(frame.Room)) (openings ??= new List<int>()).Add(id);
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
    /// Purges entities the server says are gone (destroyed, or out of the area of interest); without it a
    /// definition stays for ever, colliding and sounding. Returns the ids that were tracked, so the caller
    /// can stop their voices.
    /// </summary>
    public List<int> RemoveEntities(IEnumerable<int> entityIds)
    {
        var removed = new List<int>();
        foreach (int id in entityIds)
        {
            bool known = _definitions.TryRemove(id, out var gone);
            if (gone != null) Geometry.Note(gone, null);
            known |= _serverTransforms.TryRemove(id, out _);
            _serverVelocities.TryRemove(id, out _);
            _serverTyreDemand.TryRemove(id, out _);
            _serverSignals.TryRemove(id, out _);
            _serverWheels.TryRemove(id, out _);
            _audioEntityIds.TryRemove(id, out _);
            _regionEntityIds.TryRemove(id, out _);
            _markerEntityIds.TryRemove(id, out _);
            if (_crownIds.TryRemove(id, out _)) _crownsDirty = true;
            lock (_snapshotBuffer) { _held.Remove(id); _carried.Remove(id); _settled.Remove(id); }
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
    /// Takes one world-state packet into the interpolation buffer, merged by entity id with any packet
    /// held for the same tick. LiteNetLib will not fragment an unreliable send, so the server splits a
    /// big tick across packets with one Tick (NetworkService.SendStateUpdate); two entries for one tick
    /// would be a zero denominator in the interpolation, each holding half the world. By id, so a
    /// duplicated state replaces rather than doubles.
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
            while (_snapshotBuffer.Count > SnapshotHistory) _snapshotBuffer.RemoveAt(0);
            _snapshotBuffer.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        }
    }

    /// <summary>
    /// Moves remote entities between the buffered server snapshots, <see cref="InterpolationDelay"/>
    /// behind the newest. Once per game loop.
    /// </summary>
    public void UpdateInterpolation(float dt, int localPlayerId)
    {
        if (_snapshotBuffer.Count < 2) return;

        bool moved = false;
        lock (_snapshotBuffer)
        {
            double latestServerTime = _snapshotBuffer.Last().Tick * PhysicsConstants.FixedDeltaTime;
            double oldestServerTime = _snapshotBuffer[0].Tick * PhysicsConstants.FixedDeltaTime;
            double target = latestServerTime - InterpolationDelay;
            if (_clientInterpolationTime == 0) _clientInterpolationTime = target;

            // The playback clock follows the server's. Advanced by the client's dt alone it drifted, and
            // past the buffer every entity froze, silently ("some of the cars stop in front of me, then
            // keep going"). Corrected by rate, up to 10 % either way: a jump moves the whole world at
            // once. Only a gap past MaxDriftBeforeSnap, a stalled stream, is jumped.
            double error = target - _clientInterpolationTime;
            if (Math.Abs(error) > MaxDriftBeforeSnap)
            {
                _clientInterpolationTime = target;
                Serilog.Log.Debug("Interpolation clock resynchronised: {Error:F2} s out, {Count} snapshot(s) buffered.",
                                  error, _snapshotBuffer.Count);
            }
            else
            {
                _clientInterpolationTime += dt * Math.Clamp(1.0 + error * 2.0, 0.9, 1.1);
            }

            // Never outside what the buffer can serve.
            if (_clientInterpolationTime > latestServerTime) _clientInterpolationTime = latestServerTime;
            if (_clientInterpolationTime < oldestServerTime) _clientInterpolationTime = oldestServerTime;

            ServerStateUpdate? from = null;
            ServerStateUpdate? to = null;
            int toIndex = -1;

            for (int i = 0; i < _snapshotBuffer.Count - 1; i++)
            {
                double t0 = _snapshotBuffer[i].Tick * PhysicsConstants.FixedDeltaTime;
                double t1 = _snapshotBuffer[i+1].Tick * PhysicsConstants.FixedDeltaTime;

                if (_clientInterpolationTime >= t0 && _clientInterpolationTime <= t1)
                {
                    from = _snapshotBuffer[i];
                    to = _snapshotBuffer[i+1];
                    toIndex = i + 1;
                    break;
                }
            }

            if (from != null && to != null)
            {
                if (from.Tick != _heldThrough) HoldThrough(from, localPlayerId);

                // The next state of everything after the playback point: 'to' itself for whatever goes every
                // tick, a later snapshot for a far thing between its states.
                _next.Clear();
                for (int i = toIndex; i < _snapshotBuffer.Count; i++)
                    foreach (var s in _snapshotBuffer[i].States)
                        _next.TryAdd(s.EntityId, (_snapshotBuffer[i].Tick, s));

                _missing.Clear();
                for (int i = 1; i < _snapshotBuffer.Count && _missing.Count < SnapshotHistory; i++)
                    for (long t = _snapshotBuffer[i - 1].Tick + 1; t < _snapshotBuffer[i].Tick && _missing.Count < SnapshotHistory; t++)
                        _missing.Add(t);

                foreach (var stateTo in to.States)
                {
                    if (stateTo.EntityId == localPlayerId || _held.ContainsKey(stateTo.EntityId)) continue;
                    // Never heard of before: it starts where it is.
                    Place(stateTo.EntityId, stateTo.Transform.ToTransform(), stateTo.LinearVelocity, stateTo);
                    if (stateTo.Wheels != null) _serverWheels[stateTo.EntityId] = stateTo.Wheels;
                    moved = true;
                }

                foreach (var (id, held) in _held)
                {
                    if (id == localPlayerId) continue;
                    bool hasNext = _next.TryGetValue(id, out var next);
                    if (!hasNext && _settled.Contains(id)) continue;
                    Carry(id, held, hasNext, next.Tick, next.State, to.Tick, FirstMissingAfter(held.Tick, hasNext ? next.Tick : long.MaxValue), dt);
                    moved = true;
                }
            }
        }

        // Only a frame that moved something invalidates the snapshot and stamps the positions: the stamp
        // says how old a position is, and dead reckoning downstream measures from it.
        if (moved)
        {
            _positionsSampledAt = OpenFPS.Common.AudioClock.Now;
            Touch();
        }
    }

    /// <summary>
    /// Brings the held states up to the 'from' snapshot: every snapshot played through is folded in, including
    /// any a slow frame skipped. Once each time
    /// playback moves into a new pair.
    ///
    /// <para>The server sends a resting thing only a few times as it stops, then once a second
    /// (RestingStates), so what a snapshot leaves out stays where it was last put, not a few centimetres
    /// short where the interpolation had got to; and a far moving thing a few times a second, so between its
    /// states it is carried (<see cref="Carry"/>).</para>
    /// </summary>
    private void HoldThrough(ServerStateUpdate from, int localPlayerId)
    {
        // Playback went backwards (the clock was resynchronised): fold again from the start of what is held.
        if (from.Tick < _heldThrough) _heldThrough = long.MinValue;

        foreach (var snapshot in _snapshotBuffer)
        {
            if (snapshot.Tick <= _heldThrough) continue;
            if (snapshot.Tick > from.Tick) break;
            foreach (var s in snapshot.States)
            {
                _held[s.EntityId] = new Held { Tick = snapshot.Tick, State = s, Rates = DistantMotion.Rates.Of(s) };
                _settled.Remove(s.EntityId);
                // Wheels are not interpolated; a state without them means "as they were".
                if (s.Wheels != null && s.EntityId != localPlayerId) _serverWheels[s.EntityId] = s.Wheels;
            }
        }
        _heldThrough = from.Tick;
    }

    /// <summary>
    /// Where a thing is at <paramref name="time"/> from its last state at or before it (A) and, when one has
    /// arrived, its next (B). From A it is carried on the server's own numbers (DistantMotion.Predict: the
    /// speed and the turn it had). Once B is known it is steered onto it. Its velocity and heading take B's in
    /// the last tick before B, as everything sent every tick does: B was sent because they changed, and they
    /// changed then. Its position makes up the prediction's miss steadily from when B was
    /// <paramref name="learned"/> (or from A, if later): the mixer carries a position on its velocity between
    /// steps, and a miss made up in one tick, faster than the velocity says, would be heard as a small jump at
    /// the next step. For anything sent every tick this is the line from A to B, as it always was; a thing at
    /// rest stays where it was put.
    /// </summary>
    private static (Vector3 Position, Vector3 Velocity, Quaternion Rotation) Track(
        in Held a, bool hasB, long bTick, in EntityState b, double learned, long missing, double time)
    {
        var at = a.State.Transform.ToTransform();
        double tA = a.Tick * PhysicsConstants.FixedDeltaTime;
        // Before A (asked only to compare with the frame before): where its velocity says it was.
        if (time < tA) return (at.Position + a.State.LinearVelocity * (float)(time - tA), a.State.LinearVelocity, at.Rotation);
        var predicted = DistantMotion.Predict(at.Position, a.State.LinearVelocity, at.Rotation, a.Rates, (float)(time - tA));
        if (!hasB || bTick <= a.Tick) return predicted;

        double t1 = bTick * PhysicsConstants.FixedDeltaTime;
        // The last tick the prediction is known to have held: the one before B, or before a tick that never came
        // (B may be the repeat of a state sent in it), as the line through a lost tick has always gone.
        double lastTick = Math.Max(tA, (Math.Min(bTick, missing) - 1) * PhysicsConstants.FixedDeltaTime);
        // A thing at rest was not predicted to be anywhere else: it sets off in the last tick, as it did.
        double steer = a.State.LinearVelocity.LengthSquared() <= RestingSpeedSquared ? lastTick
                     : Math.Min(Math.Max(tA, learned), lastTick);
        if (time <= steer) return predicted;
        var bt = b.Transform.ToTransform();
        float alpha = time <= lastTick ? 0f : (float)Math.Min(1.0, (time - lastTick) / (t1 - lastTick));
        // From A to the next tick with nothing to carry it by: the straight line it always was.
        if (bTick - a.Tick == 1 && a.Rates.SpeedRate == 0f && a.Rates.Turn == Vector3.Zero)
            return (Vector3.Lerp(at.Position, bt.Position, alpha),
                    Vector3.Lerp(a.State.LinearVelocity, b.LinearVelocity, alpha),
                    Quaternion.Slerp(at.Rotation, bt.Rotation, alpha));
        float share = (float)Math.Min(1.0, (time - steer) / (t1 - steer));
        var miss = DistantMotion.Predict(at.Position, a.State.LinearVelocity, at.Rotation, a.Rates, (float)(t1 - tA));
        return (predicted.Position + (bt.Position - miss.Position) * share,
                predicted.Velocity + (b.LinearVelocity - miss.Velocity) * alpha,
                Quaternion.Slerp(predicted.Rotation, bt.Rotation * Quaternion.Inverse(miss.Rotation) * predicted.Rotation, alpha));
    }

    /// <summary>
    /// Places one thing for this frame (<see cref="Track"/>), easing out any correction: when its next state
    /// arrives too late to start from where it was carried to (a lost or late packet), the difference is
    /// carried as an offset that dies away over <see cref="BlendSeconds"/>, so nothing steps.
    /// </summary>
    private void Carry(int id, in Held a, bool hasB, long bTick, in EntityState b, long toTick, long missing, float dt)
    {
        double time = _clientInterpolationTime;
        long key = hasB ? bTick : -1;
        bool known = _carried.TryGetValue(id, out var c);
        // When this A's B was first seen: now, unless it was already being steered for.
        double learned = known && c.A.Tick == a.Tick && c.BTick == key ? c.Learned : time;
        var now = Track(a, hasB, bTick, b, learned, missing, time);
        // Playback going from one state to the next it was heading for is seamless by construction; anything
        // else (a next state that turned up, one that turned up late enough to be passed already, or a lost
        // tick that turned up after all) may not be.
        bool seamless = c.A.Tick == a.Tick ? c.BTick == key && c.Missing == missing : c.BTick == a.Tick;
        if (known && !seamless)
        {
            // Where the last frame's track and this one disagree, at the last frame's time: the difference is
            // the jump, and what moves between the two frames is this one's to make.
            var was = Track(c.A, c.BTick >= 0, c.BTick, c.B, c.Learned, c.Missing, c.Time);
            var then = Track(a, hasB, bTick, b, learned, missing, c.Time);
            var offRot = c.Offset ? c.OffsetRotation : Quaternion.Identity;
            c.OffsetPosition += was.Position - then.Position;
            c.OffsetVelocity += was.Velocity - then.Velocity;
            c.OffsetRotation = Quaternion.Normalize(offRot * was.Rotation * Quaternion.Inverse(then.Rotation));
            c.Offset = c.OffsetPosition.LengthSquared() <= MaxBlendMetres * MaxBlendMetres;
        }
        c.A = a;
        c.BTick = key;
        c.Learned = learned;
        c.Missing = missing;
        c.Time = time;
        c.B = hasB ? b : default;

        var position = now.Position;
        var velocity = now.Velocity;
        var rotation = now.Rotation;
        if (c.Offset)
        {
            position += c.OffsetPosition;
            velocity += c.OffsetVelocity;
            rotation = Quaternion.Normalize(c.OffsetRotation * rotation);
            float keep = MathF.Exp(-dt / BlendSeconds);
            c.OffsetPosition *= keep;
            // The velocity's within a tick or so, as anything sent every tick takes a change: held for a tenth
            // of a second it is a pitch arriving late.
            c.OffsetVelocity *= MathF.Exp(-dt / VelocityBlendSeconds);
            c.OffsetRotation = Quaternion.Slerp(Quaternion.Identity, c.OffsetRotation, keep);
            if (c.OffsetPosition.LengthSquared() < 1e-10f && c.OffsetVelocity.LengthSquared() < 1e-10f) c.Offset = false;
        }
        if (!c.Offset) { c.OffsetPosition = c.OffsetVelocity = Vector3.Zero; c.OffsetRotation = Quaternion.Identity; }
        _carried[id] = c;

        // What it is doing comes with its state: B's in the tick before B, as 'to' always gave it.
        var said = hasB && bTick == toTick ? b : a.State;
        Place(id, new Transform { Position = position, Rotation = rotation }, velocity, said);
        if (hasB && bTick == toTick && b.Wheels != null) _serverWheels[id] = b.Wheels;
        if (!hasB && !c.Offset && a.State.LinearVelocity.LengthSquared() <= RestingSpeedSquared) _settled.Add(id);
    }

    /// <summary>
    /// The first tick after a thing's state at <paramref name="tick"/> that the buffer has nothing for and that may
    /// have said something about it, or long.MaxValue. A change goes twice (RestingStates; a far change is
    /// repeated the tick after) and a far moving thing goes at least every DistantMotion.IntervalTicks, so a
    /// single lost tick hid nothing unless the thing's <paramref name="next"/> state is the tick after it, and a
    /// longer run hid nothing unless the next state came within that interval of its end.
    /// </summary>
    private long FirstMissingAfter(long tick, long next)
    {
        for (int i = 0; i < _missing.Count; i++)
        {
            long first = _missing[i], last = first;
            while (i + 1 < _missing.Count && _missing[i + 1] == last + 1) last = _missing[++i];
            if (last <= tick || first >= next) continue;
            first = Math.Max(first, tick + 1);
            if (last == first ? next == last + 1 : next <= last + DistantMotion.IntervalTicks) return first;
        }
        return long.MaxValue;
    }

    /// <summary>Puts a thing where it is this frame, with the velocity it has and what its state says of its
    /// tyres and horn.</summary>
    private void Place(int id, Transform transform, Vector3 velocity, in EntityState said)
    {
        _serverTransforms[id] = transform;
        Geometry.NoteMoved(id);
        _serverVelocities[id] = velocity;
        _serverTyreDemand[id] = said.TyreDemandFraction;
        _serverSignals[id] = said.Signals;
    }

    public void SyncState(IEnumerable<EntityState> states)
    {
        foreach (var s in states)
        {
            _serverTransforms[s.EntityId] = s.Transform.ToTransform();
            Geometry.NoteMoved(s.EntityId);
            _serverVelocities[s.EntityId] = s.LinearVelocity;
            _serverTyreDemand[s.EntityId] = s.TyreDemandFraction;
            _serverSignals[s.EntityId] = s.Signals;
            if (s.Wheels != null) _serverWheels[s.EntityId] = s.Wheels;
        }
        _positionsSampledAt = OpenFPS.Common.AudioClock.Now;
        Touch();
    }

    /// <summary>
    /// The world as the simulation and audio threads read it: one immutable copy per version, shared by
    /// everything in a frame. Never mutated once built, which is what makes sharing it across threads safe.
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
            // Stamped with the version read before the build: a mutation mid-build leaves _version ahead,
            // so stale data is never labelled fresh.
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
            GeometryVersion = GeometryVersion,
            TileMetres = TileMetres,
            Woods = _woods,
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
            snap.RainRateMmPerHour = _rainRate;
            snap.Precipitation = _precipitation;
            snap.RoadWaterMm = _roadWaterMm;
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
                Signals = _serverSignals.GetValueOrDefault(id),
                Wheels = _serverWheels.GetValueOrDefault(id),
            };
            snap.Entities[id] = s;
            if (s.Definition.Type != EntityType.StaticObject || s.Definition.Moves) snap.DynamicEntities.Add(s);
        }

        snap.AudioEntityIds.AddRange(_audioEntityIds.Keys);
        snap.RegionEntityIds.AddRange(_regionEntityIds.Keys);
        snap.MarkerEntityIds.AddRange(_markerEntityIds.Keys);

        // The static solids as triangles, as far as the last build has them (ClientGeometry).
        if (OpenFPS.Common.Geometry.TriangleGeometry.Enabled)
        {
            if (!_geometryHooked) { Geometry.Published = Touch; _geometryHooked = true; }
            var (world, stale, unindexed) = Geometry.ForSnapshot(
                CollectForGeometry,
                id => _definitions.ContainsKey(id),
                id => _serverTransforms.TryGetValue(id, out var t) ? (true, t.Position, t.Rotation) : (false, default, default));
            snap.Geometry = world;
            snap.GeometryStale = stale;
            snap.UnindexedStatics = unindexed;
        }
        return snap;
    }

    /// <summary>Every definition with the transform a snapshot would give it: what a geometry build reads.</summary>
    private IEnumerable<(EntityDefinition, Transform)> CollectForGeometry()
    {
        foreach (var kv in _definitions)
            yield return (kv.Value, _serverTransforms.GetValueOrDefault(kv.Key, kv.Value.Transform));
    }

    /// <summary>A fixed thing nothing can bump into (so in neither the static grid nor the moving things)
    /// that something looks for near you: a beacon such as a stair marker, or a named place
    /// (<see cref="NamedPlaces"/>).</summary>
    internal static bool IsMarker(EntityDefinition def)
        => def.Type == EntityType.StaticObject && !def.Moves && !def.Collider.IsSolid
           && (!string.IsNullOrEmpty(def.Identity.BeaconCategory) || NamedPlaces.Is(def));

    /// <summary>
    /// The static collision grid, made again into a new grid that replaces the old. Never refill it in
    /// place: every snapshot hands it out, and the acoustic worker and the audio thread found it half
    /// empty (on a streamed map, at every tile).
    /// </summary>
    private void RebuildGrid()
    {
        if (_staticGrid == null) return;
        var grid = new SpatialGrid<int>(10.0f, OversizeCells);
        foreach (var kvp in _definitions)
        {
            if (kvp.Value.Type == EntityType.StaticObject && !kvp.Value.Moves && kvp.Value.Collider.IsSolid)
            {
                grid.AddOverlapping(kvp.Value.Transform.Position, kvp.Value.Collider.Size, kvp.Value.Transform.Rotation, kvp.Key, isStatic: true);
            }
        }
        _staticGrid = grid;
        _gridNeedsRebuild = false;
        GridRebuilds++;
    }

    /// <summary>A static box over this many 10 m cells (a town's ground) is kept apart in the grid rather than
    /// filed in each cell: see SpatialGrid's oversize items.</summary>
    private const int OversizeCells = 400;

    /// <summary>Times the static grid has been made again. Diagnostic.</summary>
    public int GridRebuilds { get; private set; }

    // ── A map streamed in tiles (docs/WORLD_STREAMING.md) ───────────────────────────────────────────
    //
    // After each TileStreamUpdate the acoustic map is made again from what is held, on a niced thread,
    // and its tables swapped into the map object in use. Keep the object: the mixer drops every reverb
    // bus when it changes (FmodAudioProvider.SetAcousticMap), while a room that stays loaded keeps its id
    // and bus. The swap bumps GeometryVersion, and the acoustic worker rebuilds its Steam Audio scene.

    private readonly ConcurrentDictionary<TileKey, TileDetail> _tiles = new();
    private Vector3 _acousticMin;
    private float _voxelResolution = 0.5f, _occlusionFloor = 0.2f;
    private long _mapEpoch;
    private long _geometryVersion;
    private int _refreshRunning;
    private volatile bool _refreshAgain;
    /// <summary>A room or a doorway arrived, or a room left: the acoustic tables need making again. A tile
    /// of only walls and roads changes the Steam Audio scene alone: a new GeometryVersion answers it.</summary>
    private volatile bool _tablesDirty;

    /// <summary>The side of the tiles this map streams in, metres; 0 for a map sent whole.</summary>
    public float TileMetres { get; private set; }

    /// <summary>The tiles held and how much of each.</summary>
    public IReadOnlyDictionary<TileKey, TileDetail> Tiles => _tiles;

    /// <summary>Bumped each time a refresh swaps new tables into the acoustic map. See WorldSnapshot.GeometryVersion.</summary>
    public long GeometryVersion => Interlocked.Read(ref _geometryVersion);

    /// <summary>Refreshes finished. Diagnostic.</summary>
    public int AcousticRefreshes { get; private set; }
    /// <summary>Tile changes that moved only walls and roads: a new GeometryVersion, no rebuild.</summary>
    public int GeometryOnlyChanges { get; private set; }
    /// <summary>How long the last refresh took, milliseconds.</summary>
    public double LastAcousticRefreshMs { get; private set; }

    /// <summary>Runs a refresh off the calling thread. The game uses a niced thread of its own
    /// (BackgroundPriority); a test can run it in place.</summary>
    public Action<Action> RefreshRunner { get; set; } =
        work => OpenFPS.Client.Core.Platform.BackgroundPriority.RunLowered("TileAcoustics", work);

    /// <summary>What the acoustic map of this map is built with: from the manifest.</summary>
    public void ConfigureAcoustics(Vector3 acousticMin, float voxelResolution, float occlusionFloor, float tileMetres)
    {
        lock (_metaLock)
        {
            _acousticMin = acousticMin;
            _voxelResolution = voxelResolution;
            _occlusionFloor = occlusionFloor;
            TileMetres = tileMetres;
        }
        Geometry.Retile(tileMetres);
    }

    /// <summary>Notes the tiles a TileStreamUpdate says changed.</summary>
    public void NoteTiles(IEnumerable<TileState> tiles)
    {
        foreach (var t in tiles)
            if (t.Detail == TileDetail.None) _tiles.TryRemove(t.Key, out _);
            else _tiles[t.Key] = t.Detail;
    }

    /// <summary>
    /// The acoustic map for a set of definitions. On a streamed map, doorways into rooms not held (the
    /// room is in the next tile) are left out: there is nothing on the far side to hear into.
    /// </summary>
    public static AcousticMap BuildAcousticMap(IEnumerable<EntityDefinition> definitions, Vector3 mapSize, Vector3 min,
                                               float voxelResolution, float occlusionFloor, bool streamed, bool report)
    {
        var map = OpenFPS.Common.Systems.AcousticVolumeGenerator.GenerateRegions(definitions, mapSize, min,
                      voxelResolution, occlusionFloor, report: report);
        if (streamed) DropDanglingPortals(map);
        return map;
    }

    private static void DropDanglingPortals(AcousticMap map)
    {
        List<int>? dangling = null;
        foreach (var (id, (portal, _)) in map.Portals)
            if (!Holds(map, portal.RegionAId) || !Holds(map, portal.RegionBId)) (dangling ??= new List<int>()).Add(id);
        if (dangling == null) return;
        var portals = new Dictionary<int, (PortalComponent Portal, Vector3 Position)>(map.Portals);
        var frames = new Dictionary<int, OpeningFrame>(map.OpeningFrames);
        foreach (int id in dangling) { portals.Remove(id); frames.Remove(id); }
        map.Portals = portals;
        map.OpeningFrames = frames;

        static bool Holds(AcousticMap m, int region)
            => region == AcousticConstants.GlobalRegionId || region == m.GlobalEnvironmentId || m.Regions.ContainsKey(region);
    }

    /// <summary>
    /// Asks for the acoustic map to be made again from what is held, off this thread. If one is running,
    /// it runs once more when it finishes, so tiles that arrive during a refresh are never missed.
    /// </summary>
    public void RequestAcousticRefresh()
    {
        if (!_tablesDirty && !AcousticRefreshPending)
        {
            // Walls and roads only: the worker rebuilds the scene; rooms and openings are unchanged.
            Interlocked.Increment(ref _geometryVersion);
            GeometryOnlyChanges++;
            Touch();
            return;
        }
        _refreshAgain = true;
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0) return;
        RefreshRunner(() =>
        {
            try
            {
                while (_refreshAgain)
                {
                    _refreshAgain = false;
                    try { RefreshAcousticsNow(); }
                    catch (Exception ex) { Serilog.Log.Error(ex, "Tile acoustics: the refresh failed; the acoustic map stays as it was."); }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _refreshRunning, 0);
                // A request that landed between the last check and letting go.
                if (_refreshAgain) RequestAcousticRefresh();
            }
        });
    }

    /// <summary>True while a refresh is running or asked for. For tests and the log.</summary>
    public bool AcousticRefreshPending => _refreshAgain || Volatile.Read(ref _refreshRunning) != 0;

    /// <summary>
    /// Makes the acoustic map again from every definition held and swaps its tables into the map in use.
    /// Returns false if there was nothing to refresh (no map yet) or the map changed while it ran.
    /// </summary>
    public bool RefreshAcousticsNow()
    {
        AcousticMap? live;
        long epoch;
        Vector3 size, min;
        float res, floor;
        lock (_metaLock)
        {
            live = AcousticMap;
            epoch = _mapEpoch;
            size = CurrentMapSize; min = _acousticMin; res = _voxelResolution; floor = _occlusionFloor;
        }
        if (live == null) return false;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Cleared before the definitions are read: a room that arrives while this runs sets it again.
        _tablesDirty = false;
        var defs = _definitions.Values.ToList();
        var built = BuildAcousticMap(defs, size, min, res, floor, streamed: TileMetres > 0f, report: false);

        lock (_metaLock)
        {
            if (!ReferenceEquals(AcousticMap, live) || epoch != _mapEpoch) return false;
            Reconcile(built);
            // Each replaced whole: readers walk the dictionary they found (TrackRegion's build-then-swap).
            live.VoxelGrid = built.VoxelGrid;
            live.Regions = built.Regions;
            live.RegionPositions = built.RegionPositions;
            live.RegionRotations = built.RegionRotations;
            live.OpeningFrames = built.OpeningFrames;
            live.Portals = built.Portals;
        }
        Interlocked.Increment(ref _geometryVersion);
        AcousticRefreshes++;
        LastAcousticRefreshMs = sw.Elapsed.TotalMilliseconds;
        Serilog.Log.Information("Tile acoustics: {Regions} region(s), {Openings} opening(s) from {Defs} definitions in {Ms:F0} ms; {Tiles} tile(s) held.",
                                built.Regions.Count - 1, built.Portals.Count, defs.Count, LastAcousticRefreshMs, _tiles.Count);
        Touch();
        return true;
    }

    /// <summary>
    /// Brings a freshly built map up to what is held now, before it is swapped in: a door that swung while
    /// it was being built is put where it is, a room that arrived is added (its openings come with the
    /// next refresh), and anything that has gone is taken out.
    /// </summary>
    private void Reconcile(AcousticMap built)
    {
        foreach (int id in built.Regions.Keys.ToList())
        {
            if (id == built.GlobalEnvironmentId || id == AcousticConstants.GlobalRegionId) continue;
            if (_definitions.ContainsKey(id)) continue;
            built.Regions.Remove(id); built.RegionPositions.Remove(id); built.RegionRotations.Remove(id);
        }
        foreach (int id in _regionEntityIds.Keys)
        {
            if (built.Regions.ContainsKey(id) || !_definitions.TryGetValue(id, out var def)) continue;
            built.Regions[id] = def.Region;
            built.RegionPositions[id] = def.Transform.Position;
            built.RegionRotations[id] = def.Transform.Rotation;
        }
        foreach (int id in built.Portals.Keys.ToList())
        {
            if (id >= 0 && !_definitions.ContainsKey(id)) { built.Portals.Remove(id); continue; }
            if (built.OpeningFrames.TryGetValue(id, out var frame) && !built.Regions.ContainsKey(frame.Room))
            {
                built.Portals.Remove(id); built.OpeningFrames.Remove(id);
            }
        }
        foreach (var def in _definitions.Values)
        {
            if (def.Portal.RegionAId == def.Portal.RegionBId) continue;
            bool ends = (def.Portal.RegionAId == AcousticConstants.GlobalRegionId || built.Regions.ContainsKey(def.Portal.RegionAId))
                     && (def.Portal.RegionBId == AcousticConstants.GlobalRegionId || built.Regions.ContainsKey(def.Portal.RegionBId));
            if (def.Portal.ApertureSize > 0f && (ends || TileMetres <= 0f)) built.Portals[def.EntityId] = (def.Portal, def.Transform.Position);
            else built.Portals.Remove(def.EntityId);
        }
    }

    // ── Woods heard as one ──────────────────────────────────────────────────────────────────────
    //
    // A wood's trees past the hand-over distance are one source (WoodChorus): an entity of the client's
    // own, with a synthetic id below WoodChorus.FirstId, so the audio system voices it as it does a tree.

    private WoodChorus? _woods;
    /// <summary>The tree crowns held, and whether they changed since the woods were last made.</summary>
    private readonly ConcurrentDictionary<int, byte> _crownIds = new();
    private volatile bool _crownsDirty;
    private readonly Dictionary<(int, int, string), int> _woodIds = new();
    private readonly Dictionary<int, (string Key, Vector3 Centre, float Range)> _woodKeys = new();

    /// <summary>What the last making of the woods cost the calling thread, and the most it has, ms.</summary>
    public double LastWoodsMs { get; private set; }
    public double WoodsMsMax { get; set; }

    /// <summary>Whether an id is one of the client's own wood entities.</summary>
    public static bool IsWood(int entityId) => entityId <= WoodChorus.FirstId && entityId > WoodChorus.FirstId - 1_000_000;

    /// <summary>
    /// Makes the woods again from the tree crowns held: after a map loads and after tiles come or go.
    /// A wood whose key, middle and range are unchanged keeps its definition; the rest are made, changed
    /// or taken away as entities. Returns the ids taken away, for the caller to stop their voices.
    /// </summary>
    public List<int> RefreshWoods()
    {
        if (!_crownsDirty) return new List<int>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _crownsDirty = false;
        var crowns = new List<WoodChorus.Crown>();
        EntityDefinition? sample = null;
        foreach (int id in _crownIds.Keys)
        {
            if (!_definitions.TryGetValue(id, out var def)) continue;
            var sid = def.SoundEmitter.SoundId!;
            crowns.Add(new WoodChorus.Crown(id, _serverTransforms.TryGetValue(id, out var t) ? t.Position : def.Transform.Position, sid[8..]));
            sample ??= def;
        }
        var chorus = WoodChorus.Build(crowns, (cell, preset) =>
        {
            var key = (cell.X, cell.Z, preset);
            if (!_woodIds.TryGetValue(key, out int id)) _woodIds[key] = id = WoodChorus.FirstId - _woodIds.Count;
            return id;
        });
        var keep = new HashSet<int>();
        foreach (var w in chorus.Woods)
        {
            keep.Add(w.Id);
            if (_woodKeys.TryGetValue(w.Id, out var had) && had.Key == w.Key && had.Centre == w.Centre && had.Range == w.RangeMetres) continue;
            _woodKeys[w.Id] = (w.Key, w.Centre, w.RangeMetres);
            var def = new EntityDefinition
            {
                EntityId = w.Id,
                Type = EntityType.StaticObject,
                Transform = new Transform { Position = w.Centre, Rotation = Quaternion.Identity },
            };
            def.Identity.Name = "Woods";
            def.Material.Material = "Foliage";
            // The trees' emitter, with the wood's own key and reach.
            var em = sample!.SoundEmitter;
            em.SoundId = w.Key;
            em.Range = w.RangeMetres;
            em.Volume = 1f;
            em.ExtentMetres = w.Extent;
            def.SoundEmitter = em;
            RegisterDefinition(def);
        }
        var gone = new List<int>();
        foreach (int id in _woodKeys.Keys) if (!keep.Contains(id)) gone.Add(id);
        foreach (int id in gone) _woodKeys.Remove(id);
        if (gone.Count > 0) RemoveEntities(gone);
        _woods = chorus.Woods.Count > 0 ? chorus : null;
        Touch();
        LastWoodsMs = clock.Elapsed.TotalMilliseconds;
        WoodsMsMax = Math.Max(WoodsMsMax, LastWoodsMs);
        return gone;
    }

    /// <summary>The nearest entity within 3 m that is not a player or a wood.</summary>
    public int? GetClosestEntityId(Vector3 pos)
    {
        int? bestId = null;
        float bestDist = float.MaxValue;

        foreach (var kvp in _definitions)
        {
            if (kvp.Value.Type == EntityType.Player || IsWood(kvp.Key)) continue;
            var transform = _serverTransforms.GetValueOrDefault(kvp.Key);
            float d = Vector3.Distance(pos, transform.Position);
            if (d < bestDist) { bestDist = d; bestId = kvp.Key; }
        }
        return bestDist < 3.0f ? bestId : null;
    }
}

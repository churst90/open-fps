using System.Numerics;
using OpenFPS.Client.Services;
using OpenFPS.Common;
using OpenFPS.Common.Components;
using OpenFPS.Audio.Core;
using OpenFPS.Audio.Data;
using Serilog;
using OpenFPS.Acoustics;
using OpenFPS.Materials;

namespace OpenFPS.Client.Core;

/// <summary>
/// The game's side of its audio world (docs/SOUND_LIBRARY_BOUNDARY.md, stage 7): it fills the world's sources from
/// the client's snapshot once a frame (<see cref="SnapshotSources"/>), says where the local player is, runs the
/// game's own sound features (the one-off sounds the server reports, the beacons, the driving aids, the birds) at
/// their places in the frame, names the place the player stands in, and turns the session's events (a footfall, a
/// packet of somebody talking, a model edited) into the world's. The sound itself is <see cref="AudioWorld"/>'s.
/// </summary>
public sealed class ClientAudioSystem : IAudioHost
{
    private readonly AudioEngineFacade _audio;
    private readonly LocalPlayerState _state;
    private readonly DrivingAids _drivingAids;
    private readonly BeaconAids _beacons;
    private readonly AudioWorld _world;
    /// <summary>Beacon categories, policies and the player's switches. See BeaconAids.</summary>
    public BeaconAids Beacons => _beacons;
    /// <summary>The driver's cues and readout. See DrivingAids.</summary>
    public DrivingAids Driving => _drivingAids;

    /// <summary>The audio world this system is the host of.</summary>
    public AudioWorld World => _world;

    /// <summary>The short sounds the server reports (WorldAudioEvent), decoded into the world's one-off sounds.</summary>
    public WorldAudioPlayer WorldAudio { get; }

    /// <summary>This frame's sources, from the snapshot: what the audio world reads of the entities.</summary>
    private readonly SnapshotSources _sources = new();

    /// <summary>The snapshot before this frame's, for the rooms that moved.</summary>
    private WorldSnapshot? _previous;

    /// <summary>
    /// The audio update's cap, here so both heads share it (<see cref="AudioWorld.UpdateHz"/>). Their loop polls the
    /// network at about 200 Hz (a 5 ms sleep); nothing in the update resolves faster than a frame.
    /// </summary>
    public const double UpdateHz = AudioWorld.UpdateHz;
    private readonly UpdateThrottle _throttle = new(UpdateHz);
    /// <summary>Seconds since this system was built. A stopwatch in the game; a test hands in its
    /// own, so it can tick the throttled update and step past the engine hold without sleeping.</summary>
    private readonly Func<double> _now;

    /// <summary>The player's own entity, set at spawn: the rays leave it out, and a player on the wire
    /// never blips or is heard at its own ears.</summary>
    public int OwnEntityId
    {
        get => _world.OwnEntityId;
        set => _world.OwnEntityId = value;
    }

    /// <summary>True while the microphone is open (V). The session sets it.</summary>
    public bool OwnVoiceLive
    {
        get => _world.OwnVoiceLive;
        set => _world.OwnVoiceLive = value;
    }

    /// <summary>With <paramref name="prewarm"/> false the city's doors are not rendered in the background
    /// at start: a session with no sound has nothing to play them on.</summary>
    public ClientAudioSystem(AudioEngineFacade audio, SoundMappingService sounds, LocalPlayerState state, bool prewarm = true)
        : this(audio, sounds, state, StopwatchClock(), prewarm: prewarm) { }

    /// <summary>For tests: the same system on a clock the caller controls.</summary>
    /// <param name="audio">The engine to play through.</param>
    /// <param name="sounds">The recorded sounds by material and action.</param>
    /// <param name="state">The local player.</param>
    /// <param name="clock">Seconds, as the test steps them.</param>
    /// <param name="manualAcoustics">The acoustic worker runs no thread: the test steps it between
    /// updates (<see cref="AsyncAcousticWorker.StepForTest"/>), the rain survey runs in place, and no
    /// door or thunder renders start in the background. For the emitter-stream replay, which must come
    /// out the same on every run.</param>
    /// <param name="seed">Seeds what the game leaves to chance (birds, near rain drops, a footstep's
    /// jitter). Null: unseeded, as in the game.</param>
    /// <param name="prewarm">Render the city's doors in the background at start (<see cref="WorldAudioPlayer"/>).
    /// A test that plays no door has no use for minutes of a core.</param>
    /// <param name="beacons">The beacon choices; null reads the player's beacons.json. A lab render passes
    /// its own, so the player's settings neither colour it nor are written.</param>
    internal ClientAudioSystem(AudioEngineFacade audio, SoundMappingService sounds, LocalPlayerState state,
                               Func<double> clock, bool manualAcoustics = false, int? seed = null, bool prewarm = true,
                               BeaconPreferences? beacons = null)
    {
        _now = clock;
        _audio = audio;
        _state = state;
        _world = new AudioWorld(audio, sounds, this, clock, manualAcoustics, seed, prewarm);
        // The game's features place their voices through the world's registry, heard by its worker's answers.
        _drivingAids = new DrivingAids(audio) { Voices = _world.Voices };
        // A beacon behind a wall is not blipped: the world's acoustics.
        _beacons = new BeaconAids(audio, beacons, acoustics: _world.Acoustics) { Voices = _world.Voices, Worker = _world.AcousticWorker };
        // A placed beacon whose category is off has no voice.
        _sources.Silenced = def => def.Type == EntityType.Beacon && !_beacons.IsOn(def.Identity.BeaconCategory);
        // The wire's events, decoded into the world's one-off sounds.
        WorldAudio = new WorldAudioPlayer(_world.OneOffs);
    }

    private static Func<double> StopwatchClock()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return () => clock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// Called every frame from the game loop (throttled to <see cref="UpdateHz"/>): the frame's sources from
    /// <paramref name="world"/>, then the audio world's frame over it (<see cref="AudioWorld.Tick"/>). The snapshot is
    /// the world input as it is: immutable once built, which the acoustic worker's own thread needs.
    /// </summary>
    public void Update(WorldSnapshot world)
    {
        if (!_throttle.ShouldRun(_now())) return;
        // The previous snapshot, kept before assigning: comparing `world` with the last after it compared it
        // with itself and silently disabled the moving-region check. The two may also be the same object (an
        // unchanged world), and a zero delta is then right.
        _previous = _sources.Filled ? _sources.World : null;
        _sources.Refill(world);
        _world.Tick(world, _sources);
    }

    /// <summary>Called when a map starts loading and again at spawn (AudioWorld.NoteSceneLoading).</summary>
    public void NoteSceneLoading() => _world.NoteSceneLoading();

    /// <summary>Silences everything an entity made sound with, once the server says it is gone.</summary>
    public void ForgetEntity(int entityId) => _world.ForgetEntity(entityId);

    /// <summary>Silences the whole world, for leaving it.</summary>
    public void LeaveWorld(IEnumerable<int> entityIds) => _world.LeaveWorld(entityIds);

    /// <summary>
    /// A model was changed in the world editor (ModelUpdate): every voice playing it is stopped, and the
    /// next update starts it again from the new model, so the change is heard at once. The levels and
    /// headroom read from models are forgotten too, since they are the old model's. Returns the
    /// entities whose voices were restarted.
    /// </summary>
    public List<int> ModelChanged(string kind, string id, WorldSnapshot snapshot)
    {
        _world.ModelsChanged();
        var restarted = new List<int>();
        foreach (var (entityId, entity) in snapshot.Entities)
        {
            string sound = entity.Definition?.SoundEmitter.SoundId ?? "";
            // A loudspeaker is named beside the recording it plays, not in its sound id.
            if (kind == OpenFPS.Common.ModelLibrary.Kinds.Loudspeaker
                && id.Equals(entity.Definition?.SoundEmitter.Loudspeaker ?? "", StringComparison.OrdinalIgnoreCase))
            {
                ForgetEntity(entityId);
                restarted.Add(entityId);
                continue;
            }
            if (!OpenFPS.Common.Editing.ModelKinds.TryModelOfSound(sound, out var k, out var i)) continue;
            if (k != kind || !i.Equals(id, StringComparison.OrdinalIgnoreCase)) continue;
            ForgetEntity(entityId);
            restarted.Add(entityId);
        }
        return restarted;
    }

    /// <summary>The map's level crossings, as the rails lie (null on a map without).</summary>
    public void SetCrossings(IReadOnlyList<CrossingRails>? crossings)
        => _world.SetCrossings(crossings?.Select(c =>
        {
            var (left, right) = c.RailPoints();
            return new RailCrossing(c.Centre, c.Along, c.HalfLengthMetres, left, right);
        }).ToArray());

    /// <summary>
    /// A 20 ms packet of somebody talking, into their stream (TalkerStream), which a voice at their mouth
    /// reads (AudioWorld's talkers). Decoded with the game's codec.
    /// </summary>
    public void ReceiveVoice(int senderId, ushort sequence, byte[] opusData)
        => _audio.TalkerStreams.For(senderId, OpenFPS.Client.Core.Platform.OpusVoiceDecoder.Create).Receive(sequence, opusData, _audio.Clock.Now);

    /// <summary>Somebody else's step: a sound left where it fell, and only one close enough to hear is
    /// made at all.</summary>
    public void OnPlayerFootstep(Vector3 pos, string mat, string var, StepSlope slope = StepSlope.Level)
        => OnPlayerFootstep(pos, mat, var, slope, bodyId: -1);

    /// <summary>...with <paramref name="bodyId"/>, whose foot it was: their own body is not a wall between
    /// their foot and you.</summary>
    public void OnPlayerFootstep(Vector3 pos, string mat, string var, StepSlope slope, int bodyId)
        => _world.OtherFootstep(pos, mat, StrideAccumulator.SlopeDb(slope), StrideAccumulator.SlopePitch(slope), bodyId);

    /// <summary>Your own step, which rides with you at a fixed offset from your head (AudioWorld.OwnFootstep).</summary>
    public void OnOwnFootstep(Vector3 pos, string mat, string var, StepSlope slope = StepSlope.Level)
    {
        if (_audio.Options.Trace) Log.Information("[FOOT] step {Slope} on {Mat} at {Pos}", slope, mat, pos);
        _world.OwnFootstep(pos, mat, StrideAccumulator.SlopeDb(slope), StrideAccumulator.SlopePitch(slope));
    }

    /// <summary>Your own landing: under your own head, and it stays there.</summary>
    public void OnOwnLand(Vector3 pos, string mat, string var) => _world.OnOwnLand(pos, mat, var);

    /// <summary>Somebody else's landing, where it fell.</summary>
    public void OnPlayerLand(Vector3 pos, string mat, string var) => _world.OnPlayerLand(pos, mat, var);

    /// <summary>The listening-level calibration's voice (AudioWorld.PlayReferenceVoice).</summary>
    public void PlayReferenceVoice(string soundId, float gainDb) => _world.PlayReferenceVoice(soundId, gainDb);

    public void StopReferenceVoice() => _world.StopReferenceVoice();

    /// <summary>The server reports a new material underfoot (StatsUpdate): it becomes the listener
    /// region's floor material.</summary>
    public void NotifyMaterialChange(string material) => _world.NotifyMaterialChange(material);

    // ── The audio world's host ──────────────────────────────────────────────────────────────────

    /// <summary>The local player as the audio world hears from them.</summary>
    ListenerState IAudioHost.Listener => new()
    {
        Feet = _state.VisualPosition,
        Body = _state.Position,
        EyeHeight = _state.EyeHeight,
        Rotation = _state.Rotation,
        Yaw = _state.Yaw,
        Velocity = _state.Velocity,
        RidingId = _state.RidingEntityId,
        Shelter = _state.ShelterFactor,
        Indoor = _state.IsIndoor,
    };

    /// <summary>The floor under a point as the client's physics has it, not counting <paramref name="leaveOut"/>.</summary>
    float IAudioHost.GroundUnder(Vector3 at, int leaveOut)
        => PhysicsUtils.GetGroundHeight(_sources.World, at, leaveOut, out _);

    /// <summary>Once the listener is placed: the beacons and the driving aids.</summary>
    void IAudioHost.ListenerPlaced(Vector3 ear, float earSpeed)
    {
        var world = _sources.World;
        _beacons.EarSpeed = earSpeed;
        _beacons.Update(world, ear, _now(), OwnEntityId);
        _drivingAids.Update(world, _state, _now());
    }

    /// <summary>Once the rooms are set: the rooms that moved (unless the map is new) and the place's name.</summary>
    void IAudioHost.RoomsSet(Vector3 ear, int listenerRegion, bool newRooms)
    {
        var world = _sources.World;
        if (world.AcousticMap != null && !newRooms)
        {
            // Regions that moved. Over the region list, not the world: walking every entity is six
            // thousand struct copies a frame on the city for six hundred regions, almost none movable.
            foreach (int regionId in world.RegionEntityIds)
            {
                if (world.Entities.TryGetValue(regionId, out var snap))
                {
                    if (_previous != null && _previous.Entities.TryGetValue(snap.Id, out var oldSnap))
                    {
                        if (Vector3.Distance(snap.Transform.Position, oldSnap.Transform.Position) > 0.1f ||
                            Math.Abs(Quaternion.Dot(snap.Transform.Rotation, oldSnap.Transform.Rotation)) < 0.999f)
                        {
                            OpenFPS.Acoustics.Systems.AcousticVolumeGenerator.UpdateRegion(world.AcousticMap, snap.Id, snap.Transform.Position, snap.Transform.Rotation, snap.Definition.Region.ToRoom());
                        }
                    }
                }
            }
        }
        UpdateAcousticState(world, ear, listenerRegion);
    }


    // ── The place's name ────────────────────────────────────────────────────────────────────────

    private void UpdateAcousticState(WorldSnapshot world, Vector3 eyePos, int regId)
    {
        _state.Temperature = world.Temperature;

        _state.PrecipitationIntensity = world.PrecipitationIntensity * (1.0f - _state.ShelterFactor);

        // The place's name, by the boxes: in a doorway (the room on your side of it, for sound) you are
        // told you are in the doorway.
        int zone = _world.Acoustics.GetZoneAt(world, eyePos);
        _state.CurrentRegionId = zone;
        _state.CurrentRoomId = _world.Acoustics.GetRoomAt(world, eyePos);
        if (world.AcousticMap != null && world.AcousticMap.Regions.TryGetValue(regId, out var reg))
        {
            _state.IsIndoor = reg.IsIndoor;
        }
        else
        {
            _state.IsIndoor = false;
        }
        string name = NamedPlaces.NameOf(world, zone)
                      ?? NameOfPlace(world.AcousticMap, zone, _state.CurrentMaterial, _state.ShelterFactor);
        // A zone stops at its walls, so a doorway, the wall's own thickness, is in none: it is named
        // from the zones either side of it.
        if (name == UnderShelter) name = DoorwayName(world, eyePos) ?? name;
        _state.CurrentRegion = name;
    }

    /// <summary>Start of the name of a roofed gap between zones, such as a doorway.</summary>
    internal const string DoorwayPrefix = "doorway";

    private Vector3 _doorwayAt = new(float.NaN);
    private string? _doorwayName;

    /// <summary>
    /// "doorway between A and B" when named zones lie within a doorway's depth either side of the
    /// listener, "doorway to A" for one, null for none. Looked up again only after a step.
    /// </summary>
    private string? DoorwayName(WorldSnapshot world, Vector3 at)
    {
        if (Vector3.DistanceSquared(at, _doorwayAt) < 0.01f) return _doorwayName;
        _doorwayAt = at;
        return _doorwayName = NameOfGap(world, at, p => _world.Acoustics.GetZoneAt(world, p));
    }

    internal static string? NameOfGap(WorldSnapshot world, Vector3 at, Func<Vector3, int> regionAt)
    {
        if (world.AcousticMap == null) return null;
        var names = new List<string>(2);
        foreach (float reach in DoorwayProbeMetres)
            foreach (var d in DoorwayProbeDirections)
            {
                int id = regionAt(at + d * reach);
                string? name = NamedPlaces.NameOf(world, id);
                if (name == null && id != AcousticConstants.GlobalRegionId && world.AcousticMap.Regions.TryGetValue(id, out var r)
                    && !string.IsNullOrWhiteSpace(r.FriendlyName)) name = r.FriendlyName;
                if (name != null && !names.Contains(name)) names.Add(name);
            }
        return names.Count switch
        {
            0 => null,
            1 => $"{DoorwayPrefix} to {names[0]}",
            _ => $"{DoorwayPrefix} between {names[0]} and {names[1]}",
        };
    }

    // A door frame and the wall it is in are under half a metre deep; a vestibule a little more.
    private static readonly float[] DoorwayProbeMetres = { 0.5f, 1.0f };
    private static readonly Vector3[] DoorwayProbeDirections =
        { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ };

    /// <summary>The name of a roofed spot no zone covers: a doorway between two rooms, a gap between
    /// two zones' boxes. A condition, not a place, so it is never announced on its own.</summary>
    internal const string UnderShelter = "Under Shelter";

    /// <summary>
    /// What to call where the listener stands. A named place says its name; everywhere else is named
    /// from the ground underfoot (the footstep probe's material), because stepping off the kerb is the
    /// most navigationally important fact on a city street and "outside" says nothing. The map-wide
    /// outdoor region is always in the acoustic map and is not a named place: its name is used only for
    /// ground the material table does not know.
    /// </summary>
    internal static string NameOfPlace(AcousticMap? map, int regionId, string material, float shelter)
    {
        Room? global = null;
        if (map != null && map.Regions.TryGetValue(regionId, out var reg))
        {
            if (regionId != AcousticConstants.GlobalRegionId && !string.IsNullOrWhiteSpace(reg.FriendlyName))
                return reg.FriendlyName;
            if (regionId == AcousticConstants.GlobalRegionId) global = reg;
        }
        if (shelter > 0.8f) return UnderShelter;
        string ground = OutdoorNameFor(material);
        if (ground == Outside && !string.IsNullOrWhiteSpace(global?.FriendlyName)) return global.Value.FriendlyName;
        return ground;
    }

    /// <summary>What to call open ground by its surface (the material's <see cref="MaterialProperties.GroundName"/>);
    /// <see cref="Outside"/> for a surface that has none.</summary>
    internal static string OutdoorNameFor(string material) => AcousticRegistry.GroundNameOf(material) ?? Outside;

    /// <summary>Open ground of no recognised surface, outside every zone.</summary>
    internal const string Outside = "outside";
}

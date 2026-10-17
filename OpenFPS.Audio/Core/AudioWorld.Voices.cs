using System.Numerics;
using OpenFPS.Client.AudioEngine.Acoustics;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// The voices heard by their owner's path (docs/SPECIAL_CASES_AUDIT.md, P3a and P3b): an engine, its intake
/// and cabin paths, a borrowed far car, a horn held or honked, a siren, a train's voices, a standing machine,
/// a tree or a wood and its places, a recording. Each is declared to the registry by its owner and handed its
/// path here, from its owner's one answer this frame, so no voice has a second writer and none keeps the path
/// it started with.
/// </summary>
public partial class AudioWorld
{
    /// <summary>Every voice and the ids they are under; a question's id kept as long as the worker keeps a
    /// source nobody asks about.</summary>
    private readonly VoiceRegistry _voices = new()
    {
        ProbeHoldFrames = (int)Math.Ceiling(AsyncAcousticWorker.SaSourceTtlMs / 1000.0 * UpdateHz),
    };

    /// <summary>Every voice of this world and the ids they are under: the host's features place theirs through it.</summary>
    public VoiceRegistry Voices => _voices;

    /// <summary>An owner's path this frame: the worker's answer under the one freshness rule
    /// (<see cref="AcousticPathData.Answers"/>), with the vehicles in the way put back
    /// (<see cref="VehicleShadow"/>); or, with no answer, a stand-in (<see cref="FirstAnswer"/>).</summary>
    private readonly struct OwnerPath
    {
        /// <summary>The worker's answer as it came, for what reads the scene alone (an engine's echoes).</summary>
        public readonly AcousticPathData Raw;
        /// <summary>What reaches the ear: the answer with the shadow of what moves.</summary>
        public readonly AcousticPathData Heard;
        /// <summary>An answer for this source and this ear. A stand-in places the voice and leaves its tone
        /// as it was: refused, a voice keeps what it has.</summary>
        public readonly bool Fresh;

        public OwnerPath(AcousticPathData raw, AcousticPathData heard, bool fresh) { Raw = raw; Heard = heard; Fresh = fresh; }
    }

    /// <summary>This frame's paths by owner, so every voice of one thing is heard through the same answer.</summary>
    private readonly Dictionary<int, OwnerPath> _ownerPaths = new();

    /// <summary>The owner's path this frame, worked out once.</summary>
    private OwnerPath PathOf(IAcousticWorld world, AudioSource owner, Vector3 ear)
    {
        if (_ownerPaths.TryGetValue(owner.Id, out var known)) return known;
        var at = owner.Emission;
        OwnerPath p;
        if (_acousticWorker.TryGetDirectFor(owner.Id, at, owner.State.Velocity.Length(), ear, _earSpeed, out var direct))
            p = Shadowed(world, owner.Id, direct, at, ear);
        else
        {
            var standIn = FirstAnswer(world, owner, ear);
            p = new OwnerPath(standIn, standIn, fresh: false);
        }
        _ownerPaths[owner.Id] = p;
        return p;
    }

    /// <summary>
    /// What a chooser (which cars, which standing machines, which of their places get a voice) takes for how
    /// much a thing's path blocks it: nothing. The worker is asked only about what already has a voice, so only
    /// those have a path to count. Counted for them alone, a voiced machine behind a wall ranks under an
    /// unvoiced one behind the same wall and gives it its voice; the newcomer's path then comes back as blocked
    /// too, and the two change places each time the worker lets the loser's answer go (5 s). The budget
    /// (VoiceManager.Audibility) counts the path, among the voices it holds, which all carry theirs. To count
    /// it here the worker would have to be asked about every contender, voiced or not.
    /// </summary>
    private const float UnaskedOcclusion = 0f;

    /// <summary>An answer with the vehicles between the source and the ear put back as barriers.</summary>
    private OwnerPath Shadowed(IAcousticWorld world, int ownerId, in AcousticPathData direct, Vector3 at, Vector3 ear)
    {
        var heard = direct;
        VehicleShadow.Apply(ref heard, world, ownerId, at, ear, Listener.RidingId);
        return new OwnerPath(direct, heard, fresh: true);
    }

    /// <summary>
    /// Declares a voice of a moving thing and hands it its owner's path: where it is, where it is heard from
    /// (<see cref="VoiceRegistry.HeardFromOf"/>), and everything the path does to it, in the one emitter
    /// that places it. The caller starts or re-places it as it always has.
    /// </summary>
    private void HandPath(ref SpatialEmitter e, VoiceKind kind, IAcousticWorld world, AudioSource owner,
                          Vector3 at, float levelDb, Vector3 ear, bool inside = false)
    {
        _voices.Declare(e.EntityId, owner.Id, kind,
                        VoiceRegistry.MountOf(owner.State.Position, owner.State.Rotation, at), levelDb, _frameCount);
        if (kind is VoiceKind.Horn or VoiceKind.HeldHorn or VoiceKind.Siren or VoiceKind.TrainField or VoiceKind.TrainSignal)
            VehiclePathsAsked++;
        e.Position = at;
        if (inside || VoiceRegistry.HeardFromOf(kind) == HeardFrom.Inside)
        {
            // Inside, the voice has already rendered what gets through the body: a path would count the
            // panels twice, and its room is yours (the cabin), whatever room the car's middle is in.
            e.ApparentPosition = at;
            e.EffectiveDistance = OpenFPS.Client.AudioEngine.Core.Engine.CabinPaths.PlacedMetres;
            e.Occlusion = 0f;
            e.EqLow = 1f; e.EqMid = 1f; e.EqHigh = 1f;
            e.AirLowDb = 0f; e.AirMidDb = 0f; e.AirHighDb = 0f;
            e.ApertureFactor = 1f;
            e.TransmissionBleed = 0f;
            e.TargetRegionId = _listenerRegion;
            e.RoomGain = 1f;
            e.CarriesPath = true;
            return;
        }
        var path = PathOf(world, owner, ear);
        var heard = path.Heard;
        e.ApparentPosition = VoiceRegistry.HeardFromOf(kind) switch
        {
            HeardFrom.TowardEdge => SirenApparent(heard, at, ear),
            HeardFrom.Edge => heard.ApparentPosition,
            _ => at,
        };
        e.Occlusion = heard.Occlusion;
        e.EqLow = heard.EqLow; e.EqMid = heard.EqMid; e.EqHigh = heard.EqHigh;
        e.AirLowDb = heard.AirLowDb; e.AirMidDb = heard.AirMidDb; e.AirHighDb = heard.AirHighDb;
        e.ApertureFactor = heard.ApertureFactor;
        e.TransmissionBleed = heard.TransmissionBleed;
        e.EffectiveDistance = heard.EffectiveDistance;
        e.TargetRegionId = heard.RegionId;
        e.RoomGain = path.Fresh ? heard.RoomGain : 1f;
        e.CarriesPath = path.Fresh;
    }

    /// <summary>What an entity's own voice is: a moving thing's engine or aeroplane, a wood, a standing
    /// physical model, or a recording (anything authored, a loudspeaker's render).</summary>
    private VoiceKind OwnVoiceKind(in SourceSpec spec, int id, bool physical)
        => OwnVoiceKind(spec.IsSynth, VoiceKey(spec), id, physical);

    private VoiceKind OwnVoiceKind(bool synth, string key, int id, bool physical)
    {
        if (!synth) return VoiceKind.Recording;
        return OpenFPS.Common.ModelKindTable.KindOf(key) switch
        {
            { Body: OpenFPS.Common.ModelBody.Rolls } when OpenFPS.Common.ModelKindTable.KnownVehiclePreset(key) != null => VoiceKind.Engine,
            { Synth: OpenFPS.Common.ModelSynth.Aircraft } => VoiceKind.Aircraft,
            _ when IsWood(id) => VoiceKind.Wood,
            _ => physical ? VoiceKind.Machine : VoiceKind.Recording,
        };
    }

    private OpenFPS.Common.WoodChorus? _woodsOf;
    private readonly HashSet<int> _woodIds = new();

    /// <summary>Whether an entity is one of the woods the world made of its far trees (WoodChorus), by the
    /// woods it has now rather than by where its id falls.</summary>
    private bool IsWood(int entityId)
    {
        var woods = _sources.Woods;
        if (!ReferenceEquals(woods, _woodsOf))
        {
            _woodsOf = woods;
            _woodIds.Clear();
            if (woods != null) foreach (var w in woods.Woods) _woodIds.Add(w.Id);
        }
        return _woodIds.Contains(entityId);
    }
}

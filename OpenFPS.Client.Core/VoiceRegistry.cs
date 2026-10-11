using System.Numerics;

namespace OpenFPS.Client.Core;

/// <summary>What a registered voice is: which part of which thing it is the sound of.</summary>
internal enum VoiceKind : byte
{
    /// <summary>A vehicle's own engine, under the vehicle's entity id, through the voice budget.</summary>
    Engine,
    /// <summary>An aeroplane's or a helicopter's own voice, under its entity id, through the budget.</summary>
    Aircraft,
    /// <summary>A vehicle's front outlet (where it breathes), read off its live engine.</summary>
    Intake,
    /// <summary>A car too far off for its own engine, reading a near car's ring.</summary>
    Borrowed,
    /// <summary>A honk the server reported.</summary>
    Horn,
    /// <summary>A horn its driver is holding down.</summary>
    HeldHorn,
    /// <summary>A siren head.</summary>
    Siren,
    /// <summary>One of a train's field voices: a group of its sources, placed where their power is.</summary>
    TrainField,
    /// <summary>A train's horn or bell while it sounds.</summary>
    TrainSignal,
    /// <summary>A path into the cabin the listener sits in, riding with the head.</summary>
    Cabin,
    /// <summary>A physical model on a thing that stays where it is: a machine, a tree, a fire, a fountain's
    /// tap, a bell. Its own voice, under its entity id.</summary>
    Machine,
    /// <summary>A wood heard as one (WoodChorus), under the wood's own entity id.</summary>
    Wood,
    /// <summary>An authored recording's own voice, or the render a loudspeaker plays of one.</summary>
    Recording,
    /// <summary>One of the outer places of a tree, a fire, a fountain's tap or a wood (ExtendedSources).</summary>
    Place,
    /// <summary>A wall's copy of a recording, one of its source's image-source slots.</summary>
    Reflection,
    /// <summary>A wall answering a live engine, read back out of its ring (EngineReflections).</summary>
    EngineEcho,
    /// <summary>Somebody on voice chat, from their mouth.</summary>
    Talker,
    /// <summary>A surface round somebody talking, answering them.</summary>
    TalkerCopy,
    /// <summary>Your own voice's send into your room, while the microphone is open.</summary>
    OwnVoice,
    /// <summary>A surface answering your own voice.</summary>
    OwnVoiceCopy,
    /// <summary>Your own footstep or landing, riding with you.</summary>
    OwnStep,
    /// <summary>Somebody else's footstep or landing, where it fell.</summary>
    Footstep,
    /// <summary>A wall answering your own footstep.</summary>
    StepEcho,
    /// <summary>A rain patch's voice, or one of its parts.</summary>
    Rain,
    /// <summary>One drop landing near you.</summary>
    RainDrop,
    /// <summary>One bird's call.</summary>
    Bird,
    /// <summary>One beacon's blip.</summary>
    Beacon,
    /// <summary>A driving aid: a guide beep, a sensor tick, a tone, a rumble strip, a click.</summary>
    Aid,
    /// <summary>The listening-level calibration's voice, a step in front of you.</summary>
    Reference,
    /// <summary>A one-off sound the world reported (a door, a shot, a line said), or one of its copies.</summary>
    Transient,
}

/// <summary>Where a registered voice is heard from, and so whose path it takes.</summary>
internal enum HeardFrom : byte
{
    /// <summary>Its own point on the owner, now, wherever the owner's path arrives from: the engine and its
    /// outlets are dead-reckoned from where the car is, and an answer is up to ten frames old.</summary>
    Point,
    /// <summary>Its point, turned toward the edge the owner's path arrives round when the way is blocked
    /// (<see cref="ClientAudioSystem.SirenApparent"/>).</summary>
    TowardEdge,
    /// <summary>Where the owner's path says it arrives from, at that point: a recording heard round a corner.</summary>
    Edge,
    /// <summary>One place of a source with a size: its own point, and its middle's bearing moved by where it
    /// is on the source, through its middle's path.</summary>
    Spread,
    /// <summary>With the listener: inside the vehicle they sit in, in their own body, round their head. No
    /// path, since nothing is between, and its room is theirs.</summary>
    Inside,
    /// <summary>Its own point, by a path it works out there itself and carries in its emitter: a mouth, a
    /// wall's image, a drop, a bird. The owner's path is not its.</summary>
    Own,
}

/// <summary>One voice, as the registry knows it.</summary>
internal struct VoiceEntry
{
    public int VoiceId;
    /// <summary>The entity it is the sound of, or <see cref="VoiceRegistry.NoEntity"/>.</summary>
    public int OwnerId;
    /// <summary>Which world the owner's id belongs to (<see cref="VoiceRegistry.World"/>): ids start again
    /// with every map.</summary>
    public int World;
    public VoiceKind Kind;
    /// <summary>Where on the owner it sounds from, in the owner's frame (x right, y up, z forward); for a
    /// voice with no owner entity, where it is.</summary>
    public Vector3 Mount;
    /// <summary>Its declared level, dB at a metre.</summary>
    public float LevelDb;
    /// <summary>The audio frame it was last declared in.</summary>
    public int SeenFrame;
    /// <summary>A one-off, started once: an entry until its voice has stopped, not until a frame goes by
    /// without it.</summary>
    public bool Event;
}

/// <summary>
/// Every voice in the world (docs/SPECIAL_CASES_AUDIT.md, P3a and P3b): whose it is, which part, where on its
/// owner it sounds from and how loud it is declared. A sustained voice is declared each frame it sounds and is
/// no longer an entry after a frame without; a one-off is declared once and is one until it has stopped. One
/// pass hands every voice heard by its owner's path that path (<see cref="ClientAudioSystem"/>, Voices); a
/// voice that works out its own carries it, and nothing else writes either.
/// </summary>
internal sealed class VoiceRegistry
{
    /// <summary>The owner of a voice that is no entity's: rain, a bird, the aids round your head.</summary>
    public const int NoEntity = int.MinValue;

    /// <summary>How long a one-off may wait to start, frames: the budget plays it on its next tick, and a
    /// recording not yet decoded is parked and retried.</summary>
    public const int EventStartFrames = 30;

    private readonly Dictionary<int, VoiceEntry> _entries = new();
    private readonly HashSet<int> _owners = new();
    private readonly List<int> _gone = new();
    // Footsteps and the reference voice are declared from the game thread between updates.
    private readonly object _lock = new();

    /// <summary>The world entries are declared in: one more each time the listener leaves a map.</summary>
    public int World { get; private set; }

    /// <summary>The audio frame now: what a declaration without one is made in.</summary>
    public int Frame { get; set; }

    public int Count { get { lock (_lock) return _entries.Count; } }

    /// <summary>How a kind of voice is heard from, given its path.</summary>
    public static HeardFrom HeardFromOf(VoiceKind kind) => kind switch
    {
        VoiceKind.Horn or VoiceKind.HeldHorn or VoiceKind.Siren
            or VoiceKind.TrainField or VoiceKind.TrainSignal => HeardFrom.TowardEdge,
        VoiceKind.Recording => HeardFrom.Edge,
        VoiceKind.Place => HeardFrom.Spread,
        VoiceKind.Cabin or VoiceKind.OwnVoice or VoiceKind.OwnStep or VoiceKind.Aid or VoiceKind.Reference => HeardFrom.Inside,
        VoiceKind.Reflection or VoiceKind.EngineEcho or VoiceKind.Talker or VoiceKind.TalkerCopy or VoiceKind.OwnVoiceCopy
            or VoiceKind.Footstep or VoiceKind.StepEcho or VoiceKind.Rain or VoiceKind.RainDrop or VoiceKind.Bird
            or VoiceKind.Beacon or VoiceKind.Transient => HeardFrom.Own,
        _ => HeardFrom.Point,
    };

    /// <summary>Whether a kind is handed its owner's path by the pass, so its owner is asked about.</summary>
    public static bool TakesOwnersPath(VoiceKind kind)
        => HeardFromOf(kind) is HeardFrom.Point or HeardFrom.TowardEdge or HeardFrom.Edge or HeardFrom.Spread;

    /// <summary>Records a sustained voice for <paramref name="frame"/>, replacing what it was.</summary>
    public void Declare(int voiceId, int ownerId, VoiceKind kind, Vector3 mount, float levelDb, int frame)
        => Put(voiceId, ownerId, kind, mount, levelDb, frame, ev: false);

    /// <summary>Records a sustained voice for this frame.</summary>
    public void Declare(int voiceId, int ownerId, VoiceKind kind, Vector3 mount, float levelDb = 0f)
        => Put(voiceId, ownerId, kind, mount, levelDb, Frame, ev: false);

    /// <summary>Records a one-off as it is started, replacing what its id was.</summary>
    public void DeclareEvent(int voiceId, int ownerId, VoiceKind kind, Vector3 mount, float levelDb = 0f)
        => Put(voiceId, ownerId, kind, mount, levelDb, Frame, ev: true);

    private void Put(int voiceId, int ownerId, VoiceKind kind, Vector3 mount, float levelDb, int frame, bool ev)
    {
        lock (_lock)
            _entries[voiceId] = new VoiceEntry
            {
                VoiceId = voiceId, OwnerId = ownerId, World = World, Kind = kind, Mount = mount, LevelDb = levelDb,
                SeenFrame = frame, Event = ev,
            };
    }

    /// <summary>Keeps an entry for this frame as it was declared: a voice its owner places only now and then
    /// (a surface answering a voice, searched a few times a second), still sounding between.</summary>
    public void Keep(int voiceId)
    {
        lock (_lock)
        {
            if (!_entries.TryGetValue(voiceId, out var e) || e.World != World) return;
            e.SeenFrame = Frame;
            _entries[voiceId] = e;
        }
    }

    public bool TryGet(int voiceId, out VoiceEntry entry)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(voiceId, out entry) && entry.World == World) return true;
            entry = default;
            return false;
        }
    }

    /// <summary>Whether this voice is an entry.</summary>
    public bool IsRegistered(int voiceId) => TryGet(voiceId, out _);

    /// <summary>The owners of every entry heard by its owner's path, whose paths the pass asks for.</summary>
    public IReadOnlyCollection<int> Owners
    {
        get
        {
            lock (_lock)
            {
                _owners.Clear();
                foreach (var e in _entries.Values)
                    if (e.World == World && e.OwnerId != NoEntity && TakesOwnersPath(e.Kind)) _owners.Add(e.OwnerId);
                return _owners;
            }
        }
    }

    /// <summary>Every entry, as a copy, in no order a caller may rely on.</summary>
    public List<VoiceEntry> Entries { get { lock (_lock) return _entries.Values.ToList(); } }

    /// <summary>Drops every sustained entry not declared in <paramref name="frame"/> or since, and every
    /// one-off that has had its time to start and is not <paramref name="sounding"/>.</summary>
    public void Sweep(int frame, Func<int, bool>? sounding = null)
    {
        lock (_lock)
        {
            _gone.Clear();
            foreach (var (id, e) in _entries)
            {
                if (e.World != World) _gone.Add(id);
                else if (!e.Event) { if (e.SeenFrame < frame) _gone.Add(id); }
                else if (frame - e.SeenFrame > EventStartFrames && (sounding == null || !sounding(id))) _gone.Add(id);
            }
            foreach (int id in _gone) _entries.Remove(id);
        }
    }

    /// <summary>The listener left the map: no id declared so far means anything in the next.</summary>
    public void LeaveWorld()
    {
        lock (_lock)
        {
            _entries.Clear();
            World++;
        }
    }

    /// <summary>A point in the owner's frame, from one in the world.</summary>
    public static Vector3 MountOf(Vector3 ownerAt, Quaternion ownerRotation, Vector3 at)
        => Vector3.Transform(at - ownerAt, Quaternion.Inverse(ownerRotation));
}

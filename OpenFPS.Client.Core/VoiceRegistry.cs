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
    /// <summary>Never a voice: a question to the acoustic worker about a source, under an id of its own
    /// (<see cref="VoiceRegistry.ProbeFor"/>).</summary>
    Probe,
}

/// <summary>What an id was given for: <see cref="Kind"/> of <see cref="Owner"/>, the <see cref="Part"/>th of
/// them (<see cref="VoiceRegistry.IdFor"/>).</summary>
internal readonly record struct VoiceKey(VoiceKind Kind, int Owner, int Part);

/// <summary>Where a registered voice is heard from, and so whose path it takes.</summary>
internal enum HeardFrom : byte
{
    /// <summary>Its own point on the owner, now, wherever the owner's path arrives from.</summary>
    Point,
    /// <summary>Its point while the way is clear; when it is blocked, turned toward the edge the owner's path
    /// arrives round, at its own distance (<see cref="ClientAudioSystem.SirenApparent"/>): a blocked source comes
    /// from the edge it bent over.</summary>
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
///
/// It gives out the ids (<see cref="IdFor"/>): an entity's own voice is under its entity id, and every other
/// voice and every question to the acoustic worker under one id from one space, held while the voice is an
/// entry or still sounding (a voice fading out keeps its id until its fade has ended) and then free for the
/// next. So no two voices sound under one id, however many entities a map has or however long it runs.
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

    /// <summary>The first id given out, and every one after counts down from it: under every id an entity
    /// has (the server's are not negative) and over the woods', which are entities of the client's own
    /// (<see cref="OpenFPS.Common.WoodChorus.FirstId"/>).</summary>
    public const int FirstId = -1000;
    /// <summary>The last id there is to give: one over the first wood's.</summary>
    public const int LastId = OpenFPS.Common.WoodChorus.FirstId + 1;

    private struct Given
    {
        public int World;
        public VoiceKey Key;
        /// <summary>The frame it was last asked for in; it is not free before the frame after.</summary>
        public int Asked;
        /// <summary>Kept by whoever asked for it to be (<see cref="Hold"/>), however long.</summary>
        public bool Held;
    }
    private readonly Dictionary<(int World, VoiceKey Key), int> _idOf = new();
    private readonly Dictionary<int, Given> _given = new();
    /// <summary>Ids let go, taken again nearest zero first, so which id a voice gets does not depend on the
    /// order anything was let go in.</summary>
    private readonly SortedSet<int> _free = new();
    private int _next = FirstId;
    private readonly List<int> _letGo = new();

    /// <summary>How long a question's id is kept after it was last asked, frames: as long as the worker keeps
    /// what it traced for a source nobody is asking about.</summary>
    public int ProbeHoldFrames { get; init; } = 1;

    /// <summary>Told of every id as it is given, with what for: the stream replay's map from these ids to the
    /// ones voices had before (EmitterStreamReplayTests).</summary>
    public Action<int, VoiceKey>? OnGiven { get; set; }
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
        VoiceKind.Engine or VoiceKind.Aircraft or VoiceKind.Intake or VoiceKind.Borrowed
            or VoiceKind.Horn or VoiceKind.HeldHorn or VoiceKind.Siren
            or VoiceKind.TrainField or VoiceKind.TrainSignal => HeardFrom.TowardEdge,
        VoiceKind.Recording => HeardFrom.Edge,
        VoiceKind.Place => HeardFrom.Spread,
        VoiceKind.Cabin or VoiceKind.OwnVoice or VoiceKind.OwnStep or VoiceKind.Aid or VoiceKind.Reference => HeardFrom.Inside,
        VoiceKind.Reflection or VoiceKind.EngineEcho or VoiceKind.Talker or VoiceKind.TalkerCopy or VoiceKind.OwnVoiceCopy
            or VoiceKind.Footstep or VoiceKind.StepEcho or VoiceKind.Rain or VoiceKind.RainDrop or VoiceKind.Bird
            or VoiceKind.Beacon or VoiceKind.Transient or VoiceKind.Probe => HeardFrom.Own,
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

            // An id is free once it is no entry, nothing asked for it this frame and the mixer has nothing
            // under it: a voice fading out (a retiring place or outlet, a train's voice let go) holds its id.
            _letGo.Clear();
            foreach (var (id, g) in _given)
            {
                if (g.Held || _entries.ContainsKey(id)) continue;
                if (g.Asked >= (g.Key.Kind == VoiceKind.Probe ? frame - ProbeHoldFrames : frame)) continue;
                if (sounding != null && sounding(id)) continue;
                _letGo.Add(id);
            }
            foreach (int id in _letGo)
            {
                _idOf.Remove((_given[id].World, _given[id].Key));
                _given.Remove(id);
                _free.Add(id);
            }
        }
    }

    /// <summary>The listener left the map: no id declared so far means anything in the next. The ids given in
    /// it are let go as their voices stop.</summary>
    public void LeaveWorld()
    {
        lock (_lock)
        {
            _entries.Clear();
            foreach (int id in _given.Keys.ToList())
            {
                var g = _given[id];
                g.Held = false;
                _given[id] = g;
            }
            World++;
        }
    }

    /// <summary>A landing's part (<see cref="IdFor"/>): one landing at a time, the next replacing it.</summary>
    public const int Landing = -1;

    /// <summary>
    /// The id of one voice: <paramref name="kind"/> of <paramref name="owner"/>, the <paramref name="part"/>th of
    /// them. The owner is an entity, or for a voice no entity owns what it belongs to (a train's number, a rain
    /// slot, an aid, a talker's room); the part tells one of its voices of a kind from another (a slot, a place,
    /// a count). An entity's own voice is under its entity id; anything else is given the id it has, or the
    /// next free one, held until it is let go (<see cref="Sweep"/>).
    /// </summary>
    public int IdFor(VoiceKind kind, int owner, int part = 0)
    {
        if (IsOwnVoice(kind)) return owner;
        var key = new VoiceKey(kind, owner, Turn(kind, owner, part));
        lock (_lock)
        {
            if (_idOf.TryGetValue((World, key), out int id))
            {
                var g = _given[id];
                g.Asked = Frame;
                _given[id] = g;
                return id;
            }
            if (_free.Count > 0)
            {
                id = _free.Max;
                _free.Remove(id);
            }
            else
            {
                if (_next < LastId) throw new InvalidOperationException($"Every voice id from {FirstId} to {LastId} is in use.");
                id = _next--;
            }
            _idOf[(World, key)] = id;
            _given[id] = new Given { World = World, Key = key, Asked = Frame };
            OnGiven?.Invoke(id, key);
            return id;
        }
    }

    /// <summary>The id of a voice (<see cref="IdFor"/>), kept for it until the world is left, whether it sounds
    /// or not: for whoever refers to a voice by its id before it starts and after it stops (a test).</summary>
    public int Hold(VoiceKind kind, int owner, int part = 0)
    {
        int id = IdFor(kind, owner, part);
        if (IsOwnVoice(kind)) return id;
        lock (_lock)
        {
            var g = _given[id];
            g.Held = true;
            _given[id] = g;
        }
        return id;
    }

    /// <summary>The id the acoustic worker is asked about an entity's sound under when it is not one of the
    /// entity's own voices (a one-off's source, somebody talking): held a while after the last asking, as long
    /// as the worker keeps its answer (<see cref="ProbeHoldFrames"/>).</summary>
    public int ProbeFor(int entityId) => IdFor(VoiceKind.Probe, entityId);

    /// <summary>
    /// How many one-offs of a kind are voices at once: the next after that many is the first's turn again, and
    /// replaces it if it is still held (a step's, a drop's). 0: every one its own. As they were before ids were
    /// given out; whether each should be a voice of its own is a change to what is heard (P3d).
    /// </summary>
    public static int TurnsOf(VoiceKind kind, int owner) => kind switch
    {
        VoiceKind.Transient => 400_000,
        VoiceKind.Beacon => 24,
        VoiceKind.Bird or VoiceKind.StepEcho or VoiceKind.RainDrop => 48,
        VoiceKind.OwnStep => 12,
        VoiceKind.Footstep => 64,
        VoiceKind.Aid => (AidVoice)owner is AidVoice.Guide or AidVoice.Centre or AidVoice.Kerb or AidVoice.Turn or AidVoice.Brake ? 6 : 0,
        _ => 0,
    };

    /// <summary>Which turn the <paramref name="part"/>th one-off of a kind takes (<see cref="TurnsOf"/>); a
    /// landing's part, below zero, is its own.</summary>
    private static int Turn(VoiceKind kind, int owner, int part)
        => TurnsOf(kind, owner) is int turns and > 0 && part >= 0 ? part % turns : part;

    /// <summary>Whether a kind is an entity's own voice, which is under the entity's id.</summary>
    public static bool IsOwnVoice(VoiceKind kind)
        => kind is VoiceKind.Engine or VoiceKind.Aircraft or VoiceKind.Machine or VoiceKind.Wood or VoiceKind.Recording;

    /// <summary>What an id is held for now, if anything.</summary>
    public bool TryKeyOf(int id, out VoiceKey key)
    {
        lock (_lock)
        {
            if (_given.TryGetValue(id, out var g) && g.World == World) { key = g.Key; return true; }
            key = default;
            return false;
        }
    }

    /// <summary>How many ids are held now.</summary>
    public int IdsHeld { get { lock (_lock) return _given.Count; } }

    /// <summary>
    /// What a voice's chance is drawn from (<see cref="OpenFPS.Client.AudioEngine.Data.SpatialEmitter.Seed"/>),
    /// by what it is the sound of: the ids voices had before they were given out, so nothing rendered moves.
    /// An entity's own voice is seeded by its entity id.
    /// </summary>
    public static int SeedOf(VoiceKind kind, int owner, int part = 0) => SeedOfTurn(kind, owner, Turn(kind, owner, part));

    private static int SeedOfTurn(VoiceKind kind, int owner, int part) => kind switch
    {
        VoiceKind.Engine or VoiceKind.Aircraft or VoiceKind.Machine or VoiceKind.Wood or VoiceKind.Recording => owner,
        VoiceKind.Reflection => -30_000 - owner * (OpenFPS.Common.EarlyReflections.MaxArrivals + 1) - part,
        VoiceKind.Transient => -100_000 - part,
        VoiceKind.EngineEcho => -600_000 - part,
        VoiceKind.Borrowed => -700_000 - Math.Abs(owner),
        VoiceKind.Intake => -800_000 - Math.Abs(owner),
        VoiceKind.Siren => -900_000 - Math.Abs(owner),
        VoiceKind.Aid => AidSeed((AidVoice)owner, part),
        VoiceKind.Beacon => -967_000 - part,
        VoiceKind.Bird => -970_000 - part,
        VoiceKind.Horn => -1_200_000 - Math.Abs(owner),
        VoiceKind.OwnVoice => -1_300_000,
        VoiceKind.OwnVoiceCopy => -1_300_001 - part,
        VoiceKind.Talker => -1_400_000 - Math.Abs(owner),
        VoiceKind.Cabin => -1_600_000 - part,
        VoiceKind.TalkerCopy => -1_700_000 - owner * 8 - part,
        VoiceKind.StepEcho => -1_800_000 - part,
        VoiceKind.HeldHorn => -2_100_000 - Math.Abs(owner),
        VoiceKind.TrainField or VoiceKind.TrainSignal => -2_300_000 - (owner * OpenFPS.Client.AudioEngine.Core.Rail.TrainVoicing.Slots + part),
        VoiceKind.Rain => part == 0 ? -2_400_000 - owner : -2_400_100 - owner * 8 - part,
        VoiceKind.RainDrop => -2_401_000 - part,
        VoiceKind.Place => -5_000_000 - Math.Abs(owner) * OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.MaxPlaces - part,
        VoiceKind.Reference => -7_900_000 - part,
        VoiceKind.Probe => -3_000_000 - owner,
        VoiceKind.OwnStep => part == Landing ? -50 : -100 - part,
        VoiceKind.Footstep => part == Landing ? -50 : -300 - part,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "no seed"),
    };

    private static int AidSeed(AidVoice aid, int part) => aid switch
    {
        AidVoice.Guide => -961_000 - part,
        AidVoice.Centre => -962_000 - part,
        AidVoice.Kerb => -963_000 - part,
        AidVoice.CentreTone => -964_001,
        AidVoice.KerbTone => -964_002,
        AidVoice.CentreRumble => -964_003,
        AidVoice.KerbRumble => -964_004,
        AidVoice.Turn => -965_000 - part,
        AidVoice.Align => -966_001,
        AidVoice.Relay => -966_010,
        AidVoice.OverSpeed => -966_011,
        AidVoice.Brake => -967_100 - part,
        _ => throw new ArgumentOutOfRangeException(nameof(aid), aid, "no seed"),
    };

    /// <summary>A point in the owner's frame, from one in the world.</summary>
    public static Vector3 MountOf(Vector3 ownerAt, Quaternion ownerRotation, Vector3 at)
        => Vector3.Transform(at - ownerAt, Quaternion.Inverse(ownerRotation));
}

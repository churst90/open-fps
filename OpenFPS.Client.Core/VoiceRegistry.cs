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
}

/// <summary>Where a registered voice is heard from, given its owner's path.</summary>
internal enum HeardFrom : byte
{
    /// <summary>Its own point on the owner, now, wherever the path arrives from: the engine and its
    /// outlets are dead-reckoned from where the car is, and an answer is up to ten frames old.</summary>
    Point,
    /// <summary>Its point, turned toward the edge the path arrives round when the way is blocked
    /// (<see cref="ClientAudioSystem.SirenApparent"/>).</summary>
    TowardEdge,
    /// <summary>Inside the vehicle the listener sits in: no path, since its voice renders what gets
    /// through the body, and its room is the listener's.</summary>
    Inside,
}

/// <summary>One voice of a moving thing, as the registry knows it.</summary>
internal struct VoiceEntry
{
    public int VoiceId;
    /// <summary>The entity it is the sound of.</summary>
    public int OwnerId;
    /// <summary>Which world the owner's id belongs to (<see cref="VoiceRegistry.World"/>): ids start again
    /// with every map.</summary>
    public int World;
    public VoiceKind Kind;
    /// <summary>Where on the owner it sounds from, in the owner's frame (x right, y up, z forward).</summary>
    public Vector3 Mount;
    /// <summary>Its declared level, dB at a metre.</summary>
    public float LevelDb;
    /// <summary>The audio frame it was last declared in.</summary>
    public int SeenFrame;
}

/// <summary>
/// Every voice of a moving thing (docs/SPECIAL_CASES_AUDIT.md, P3a): whose it is, which part, where on its
/// owner it sounds from and how loud it is declared. Owners declare their voices each frame they sound; one
/// pass hands every entry its owner's path (<see cref="ClientAudioSystem"/>, Voices). A voice not declared in
/// a frame is no longer an entry.
/// </summary>
internal sealed class VoiceRegistry
{
    private readonly Dictionary<int, VoiceEntry> _entries = new();
    private readonly HashSet<int> _owners = new();
    private readonly List<int> _gone = new();

    /// <summary>The world entries are declared in: one more each time the listener leaves a map.</summary>
    public int World { get; private set; }

    public int Count => _entries.Count;

    /// <summary>How a kind of voice is heard from, given its path.</summary>
    public static HeardFrom HeardFromOf(VoiceKind kind) => kind switch
    {
        VoiceKind.Horn or VoiceKind.HeldHorn or VoiceKind.Siren
            or VoiceKind.TrainField or VoiceKind.TrainSignal => HeardFrom.TowardEdge,
        VoiceKind.Cabin => HeardFrom.Inside,
        _ => HeardFrom.Point,
    };

    /// <summary>Records a voice for this frame, replacing what it was.</summary>
    public void Declare(int voiceId, int ownerId, VoiceKind kind, Vector3 mount, float levelDb, int frame)
        => _entries[voiceId] = new VoiceEntry
        {
            VoiceId = voiceId, OwnerId = ownerId, World = World, Kind = kind, Mount = mount, LevelDb = levelDb,
            SeenFrame = frame,
        };

    public bool TryGet(int voiceId, out VoiceEntry entry)
    {
        if (_entries.TryGetValue(voiceId, out entry) && entry.World == World) return true;
        entry = default;
        return false;
    }

    /// <summary>Whether this voice is an entry: its path and placement are the registry pass's alone.</summary>
    public bool IsRegistered(int voiceId) => TryGet(voiceId, out _);

    /// <summary>The owners of every entry, whose paths the pass asks for.</summary>
    public IReadOnlyCollection<int> Owners
    {
        get
        {
            _owners.Clear();
            foreach (var e in _entries.Values) if (e.World == World) _owners.Add(e.OwnerId);
            return _owners;
        }
    }

    /// <summary>Every entry, in no order a caller may rely on.</summary>
    public IEnumerable<VoiceEntry> Entries => _entries.Values;

    /// <summary>Drops every entry not declared in <paramref name="frame"/> or since.</summary>
    public void Sweep(int frame)
    {
        _gone.Clear();
        foreach (var (id, e) in _entries) if (e.SeenFrame < frame || e.World != World) _gone.Add(id);
        foreach (int id in _gone) _entries.Remove(id);
    }

    /// <summary>The listener left the map: no id declared so far means anything in the next.</summary>
    public void LeaveWorld()
    {
        _entries.Clear();
        World++;
    }

    /// <summary>A point in the owner's frame, from one in the world.</summary>
    public static Vector3 MountOf(Vector3 ownerAt, Quaternion ownerRotation, Vector3 at)
        => Vector3.Transform(at - ownerAt, Quaternion.Inverse(ownerRotation));
}

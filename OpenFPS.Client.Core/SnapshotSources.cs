using System.Diagnostics.CodeAnalysis;
using OpenFPS.Client.AudioEngine.Data;
using OpenFPS.Common;
using OpenFPS.Common.Networking;

namespace OpenFPS.Client.Core;

/// <summary>
/// The audio world's sources, from the client's snapshot (docs/SOUND_LIBRARY_BOUNDARY.md, stage 7): each entity
/// turned into an <see cref="AudioSource"/> the first time it is asked about in a frame, and kept for the rest of
/// it. Refilled once a frame with that frame's snapshot, which never changes once built, so a source asked about
/// twice in a frame is the same answer.
/// </summary>
public sealed class SnapshotSources : IAudioSources
{
    private sealed class Entry
    {
        public required AudioSource Source;
        public int Frame;
    }

    private readonly Dictionary<int, Entry> _held = new();
    private readonly List<int> _gone = new();
    private WorldSnapshot _world = new();
    private int _frame;

    /// <summary>The host's switch on a thing: true has its voice stopped (a beacon whose category is off).
    /// Asked once a frame per thing, when it is first read.</summary>
    public Func<EntityDefinition, bool>? Silenced { get; set; }

    /// <summary>The snapshot the table reads this frame.</summary>
    public WorldSnapshot World => _world;

    /// <summary>Whether a frame's snapshot has been read yet.</summary>
    public bool Filled => _frame > 0;

    /// <summary>A new frame's table, read from <paramref name="world"/>. What was read of the last one is
    /// forgotten; an entity that has left is let go now and then.</summary>
    public void Refill(WorldSnapshot world)
    {
        _world = world;
        _frame++;
        if (_frame % 600 != 0) return;
        _gone.Clear();
        foreach (var (id, _) in _held) if (!world.Entities.ContainsKey(id)) _gone.Add(id);
        foreach (int id in _gone) _held.Remove(id);
    }

    public bool TryGet(int id, [NotNullWhen(true)] out AudioSource? source)
    {
        if (!_world.Entities.TryGetValue(id, out var snap)) { source = null; return false; }
        source = Read(snap);
        return true;
    }

    public bool Contains(int id) => _world.Entities.ContainsKey(id);

    public IReadOnlyList<int> Audible => _world.AudioEntityIds;
    public int MovingCount => _world.DynamicEntities.Count;
    public AudioSource MovingAt(int index) => Read(_world.DynamicEntities[index]);
    public double SampledAt => _world.PositionsSampledAt;
    public WoodChorus? Woods => _world.Woods;
    public Precipitation Precipitation => _world.Precipitation;
    public float RainRateMmPerHour => _world.RainRateMmPerHour;
    public Precipitation Falling => _world.Falling;
    public float RoadWaterMm => _world.RoadWaterMm;
    public OutdoorAir Outdoors => _world.Outdoors;

    private AudioSource Read(in EntitySnapshot snap)
    {
        if (!_held.TryGetValue(snap.Id, out var entry))
            _held[snap.Id] = entry = new Entry { Source = new AudioSource(snap.Id), Frame = _frame - 1 };
        if (entry.Frame != _frame)
        {
            entry.Frame = _frame;
            var def = snap.Definition;
            entry.Source.Set(SpecOf(def), StateOf(snap), def != null && Silenced?.Invoke(def) == true);
        }
        return entry.Source;
    }

    /// <summary>An entity's emitter as a source's spec, field by field, its offset the one its kind implies when
    /// none is authored (<see cref="EntityEmission.LocalOffset(EntityDefinition?)"/>); an empty one for an entity
    /// with no definition.</summary>
    public static SourceSpec SpecOf(EntityDefinition? def)
    {
        if (def == null) return default;
        var em = def.SoundEmitter;
        return new SourceSpec
        {
            SoundId = em.SoundId,
            StartSoundId = em.StartSoundId,
            StopSoundId = em.StopSoundId,
            Mode = em.Mode,
            Volume = em.Volume,
            Range = em.Range,
            MinDistance = em.MinDistance,
            Direction = em.Direction,
            ConeInsideAngle = em.ConeInsideAngle,
            ConeOutsideAngle = em.ConeOutsideAngle,
            ConeOutsideVolume = em.ConeOutsideVolume,
            RepeatIntervalSeconds = em.RepeatIntervalSeconds,
            IsGranular = em.IsGranular,
            GranularPosition = em.GranularPosition,
            GranularGrainSizeMs = em.GranularGrainSizeMs,
            GranularDensity = em.GranularDensity,
            GranularPitch = em.GranularPitch,
            GranularPositionJitter = em.GranularPositionJitter,
            GranularPitchJitter = em.GranularPitchJitter,
            IsSynth = em.IsSynth,
            SynthWave = em.SynthWave,
            SynthFrequency = em.SynthFrequency,
            SynthLfoRate = em.SynthLfoRate,
            SynthLfoDepth = em.SynthLfoDepth,
            SynthFilterCutoff = em.SynthFilterCutoff,
            SynthFilterResonance = em.SynthFilterResonance,
            SynthPulseWidth = em.SynthPulseWidth,
            Offset = EntityEmission.LocalOffset(em.Offset, em.IsSynth, em.SoundId),
            ExtentMetres = em.ExtentMetres,
            SynthRunning = em.SynthRunning,
            ServingStop = em.ServingStop,
            WindowsOpen = em.WindowsOpen,
            Loudspeaker = em.Loudspeaker,
            Quench = em.Quench,
            Line = em.Line,
            Shape = def.Collider.Shape,
            Size = def.Collider.Size,
        };
    }

    /// <summary>Where an entity is and what it sends of itself.</summary>
    public static SourceState StateOf(in EntitySnapshot snap) => new()
    {
        Position = snap.Transform.Position,
        Rotation = snap.Transform.Rotation,
        Velocity = snap.Velocity,
        TyreDemand = snap.TyreDemand,
        Wheels = snap.Wheels,
        Signals = snap.Signals,
        Lever = snap.Lever,
    };
}

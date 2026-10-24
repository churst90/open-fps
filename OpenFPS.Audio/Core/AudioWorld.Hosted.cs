using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using OpenFPS.Audio.Data;
using OpenFPS.Environment;
using OpenFPS.Sound;

namespace OpenFPS.Audio.Core;

/// <summary>
/// A recording a host plays at a place (<see cref="AudioWorld.Play"/>): moved, turned up or down, stopped, and asked
/// whether it still sounds. It is a source of the world like any the host holds, so it is placed by the same path
/// as a loudspeaker's recording: occluded, routed through openings, reflected, sent to the reverb, panned by HRTF,
/// ranked by the voice budget and given its voice by the registry. Safe to call from any thread; a change is heard
/// from the world's next frame.
/// </summary>
public sealed class PlacedSound
{
    private readonly object _gate;
    private Vector3 _at, _velocity;
    private float _gainDb;
    private bool _stopped, _finished, _started;

    internal PlacedSound(object gate, int id, string sound, bool loops, Vector3 at, float gainDb, float minDistance, float range, double now)
    {
        _gate = gate; Id = id; Sound = sound; Loops = loops; _at = at; _gainDb = gainDb;
        MinDistance = minDistance; Range = range; AddedAt = now;
        Source = new AudioSource(id);
    }

    /// <summary>The entity id the voice sounds under, in the band the library keeps for these
    /// (<see cref="HostedSounds.FirstId"/> up).</summary>
    public int Id { get; }
    public string Sound { get; }
    public bool Loops { get; }
    internal float MinDistance { get; }
    internal float Range { get; }
    internal AudioSource Source { get; }
    internal double AddedAt { get; }

    public Vector3 Position { get { lock (_gate) return _at; } }

    /// <summary>The gain in decibels over the recording as authored, heard at its minimum distance (0: as authored;
    /// the unit <see cref="AudioWorld.PlayReferenceVoice"/> takes).</summary>
    public float GainDb
    {
        get { lock (_gate) return _gainDb; }
        set
        {
            if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "a gain is a finite number of decibels");
            lock (_gate) _gainDb = value;
        }
    }

    /// <summary>Moves it. <paramref name="velocity"/> (metres a second, the host's own) is what its Doppler shift is
    /// worked from; leave it zero for a sound that stays, or that moves too slowly to hear.</summary>
    public void MoveTo(Vector3 position, Vector3 velocity = default)
    {
        if (!Finite(position) || !Finite(velocity)) throw new ArgumentException("a place is finite numbers");
        lock (_gate) { _at = position; _velocity = velocity; }
    }

    /// <summary>Lets it go: the voice fades out over the mixer's usual short fade (never a click), and is gone from the
    /// next frame. Does nothing if it has ended.</summary>
    public void Stop() { lock (_gate) _stopped = true; }

    /// <summary>Whether it is still wanted and has not ended: a loop until it is stopped, a once sound until it has
    /// played to its end (or could not be played at all, <see cref="HostedSounds.StartGraceSeconds"/>). True from the
    /// call that made it, before its first sound is in the mix.</summary>
    public bool IsPlaying { get { lock (_gate) return !_stopped && !_finished; } }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    // ── the world's side ─────────────────────────────────────────────────────────────────────────────────────

    internal bool Wanted { get { lock (_gate) return !_stopped && !_finished; } }
    internal bool Started { get { lock (_gate) return _started; } set { lock (_gate) _started = value; } }
    internal void End() { lock (_gate) _finished = true; }

    /// <summary>Its source for this frame.</summary>
    internal void Fill()
    {
        Vector3 at, velocity; float db;
        lock (_gate) { at = _at; velocity = _velocity; db = _gainDb; }
        Source.Set(new SourceSpec
        {
            SoundId = Sound,
            Mode = Loops ? PlaybackMode.LoopOne : PlaybackMode.Single,
            Volume = MathF.Pow(10f, db / 20f),
            MinDistance = MinDistance,
            Range = Range,
        }, new SourceState { Position = at, Rotation = Quaternion.Identity, Velocity = velocity });
    }
}

/// <summary>
/// The sounds a host has played at places, kept by the world and offered to its frame as sources beside the host's
/// own, so that nothing about their placing is special: <see cref="Begin"/> returns the host's sources with these
/// added, or the host's own when there are none.
/// </summary>
public sealed class HostedSounds
{
    /// <summary>The first entity id given to a played sound, counting up. A host's own ids stay under it.</summary>
    public const int FirstId = 2_000_000_000;

    /// <summary>How long a once sound may take to be heard before it counts as one that could not be: a file that is
    /// missing, or a voice the budget never had room for.</summary>
    public const double StartGraceSeconds = 10.0;

    private readonly object _gate = new();
    private readonly List<PlacedSound> _live = new();
    private readonly Dictionary<int, PlacedSound> _byId = new();
    private readonly List<int> _ids = new();
    private readonly List<int> _moving = new();
    private int _next = FirstId;
    private readonly Merged _merged = new();

    internal HostedSounds() { }

    public int Count { get { lock (_gate) return _live.Count; } }

    internal PlacedSound Add(string sound, bool loops, Vector3 at, float gainDb, float minDistance, float range, double now)
    {
        lock (_gate)
        {
            if (_next == int.MaxValue) throw new InvalidOperationException("no more ids for played sounds");
            var placed = new PlacedSound(_gate, _next++, sound, loops, at, gainDb, minDistance, range, now);
            _live.Add(placed);
            _byId[placed.Id] = placed;
            return placed;
        }
    }

    /// <summary>Whether a once sound's voice has been heard and is only placed from now on: asked for again it would
    /// be started over, as a loudspeaker's announcement would.</summary>
    internal bool PlacedOnly(int id)
    {
        PlacedSound? p;
        lock (_gate) _byId.TryGetValue(id, out p);
        return p is { Loops: false, Started: true };
    }

    /// <summary>The frame's sources: the host's with these added. Those that have ended or been stopped are taken out
    /// first and their ids returned in <paramref name="gone"/>, to be let go of by the caller.</summary>
    internal IAudioSources Begin(IAudioSources inner, Func<int, bool> sounding, double now, double clockNow, List<int> gone)
    {
        gone.Clear();
        lock (_gate)
        {
            if (_live.Count == 0) return inner;
            _ids.Clear(); _moving.Clear();
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var p = _live[i];
                if (p.Wanted && !p.Loops)
                {
                    // Heard once, and then not: it has played to its end. Never heard: it could not be.
                    if (!p.Started) { if (sounding(p.Id)) p.Started = true; else if (now - p.AddedAt > StartGraceSeconds) p.End(); }
                    else if (!sounding(p.Id)) p.End();
                }
                if (!p.Wanted)
                {
                    gone.Add(p.Id);
                    _byId.Remove(p.Id);
                    _live.RemoveAt(i);
                }
            }
            if (_live.Count == 0) return inner;
            foreach (var p in _live)
            {
                p.Fill();
                _ids.Add(p.Id);
                if (p.Source.State.Velocity != Vector3.Zero) _moving.Add(p.Id);
            }
            _merged.Set(inner, _byId, _ids, _moving, clockNow);
            return _merged;
        }
    }

    /// <summary>Ends every one (the world is leaving its map or being disposed): their handles read as finished.</summary>
    internal void EndAll(List<int> ids)
    {
        lock (_gate)
        {
            foreach (var p in _live) { p.End(); ids.Add(p.Id); }
            _live.Clear(); _byId.Clear();
        }
    }

    /// <summary>The host's sources and the played sounds' as one: nothing is copied but the played sounds' ids.</summary>
    private sealed class Merged : IAudioSources
    {
        private IAudioSources _inner = null!;
        private Dictionary<int, PlacedSound> _byId = null!;
        private List<int> _moving = null!;
        private double _clockNow;
        private readonly Concat _audible = new();

        public void Set(IAudioSources inner, Dictionary<int, PlacedSound> byId, List<int> hosted, List<int> moving, double clockNow)
        {
            _inner = inner; _byId = byId; _moving = moving; _clockNow = clockNow;
            _audible.Set(inner.Audible, hosted);
        }

        public bool TryGet(int id, [NotNullWhen(true)] out AudioSource? source)
        {
            if (_byId.TryGetValue(id, out var p)) { source = p.Source; return true; }
            return _inner.TryGet(id, out source);
        }
        public bool Contains(int id) => _byId.ContainsKey(id) || _inner.Contains(id);
        public IReadOnlyList<int> Audible => _audible;
        public int MovingCount => _inner.MovingCount + _moving.Count;
        public AudioSource MovingAt(int index)
            => index < _inner.MovingCount ? _inner.MovingAt(index) : _byId[_moving[index - _inner.MovingCount]].Source;
        // Played sounds are placed as of now; the host's stamp when it has one.
        public double SampledAt => _inner.SampledAt > 0 ? _inner.SampledAt : _clockNow;
        public WoodChorus? Woods => _inner.Woods;
        public Precipitation Precipitation => _inner.Precipitation;
        public float RainRateMmPerHour => _inner.RainRateMmPerHour;
        public Precipitation Falling => _inner.Falling;
        public float RoadWaterMm => _inner.RoadWaterMm;
        public OutdoorAir Outdoors => _inner.Outdoors;
    }

    private sealed class Concat : IReadOnlyList<int>
    {
        private IReadOnlyList<int> _a = Array.Empty<int>(), _b = Array.Empty<int>();
        public void Set(IReadOnlyList<int> a, IReadOnlyList<int> b) { _a = a; _b = b; }
        public int Count => _a.Count + _b.Count;
        public int this[int i] => i < _a.Count ? _a[i] : _b[i - _a.Count];
        public IEnumerator<int> GetEnumerator() { for (int i = 0, n = Count; i < n; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

public partial class AudioWorld
{
    private readonly HostedSounds _hosted = new();
    private readonly List<int> _hostedGone = new();

    /// <summary>
    /// Plays a recording at a place and gives back its handle (library 0.2, item 1). <paramref name="sound"/> is an id
    /// under <c>Paths.Sounds</c> or one the host handed in as bytes (<see cref="AudioEngineFacade.AddSound(string, byte[])"/>).
    /// <paramref name="gainDb"/> is decibels over the recording as authored, heard at <paramref name="minDistance"/>
    /// metres; it is heard to <paramref name="range"/> metres.
    ///
    /// It is placed by the world's frame (<see cref="Tick"/>) as any recording a host holds is, so it is heard only
    /// while frames are ticked, from the first one after this call. In a world with no map (an empty
    /// <see cref="IAcousticWorld"/>) there is nothing in the way and it is heard dry, at the place, with the
    /// distance, air and HRTF every voice has. A once sound ends by itself and its handle then reads as finished. Its
    /// entity id is in the band from <see cref="HostedSounds.FirstId"/> up, which a host's own ids must stay under.
    /// </summary>
    public PlacedSound Play(string sound, Vector3 at, float gainDb = 0f, bool loop = false, float minDistance = 1f, float range = 200f)
    {
        if (string.IsNullOrEmpty(sound)) throw new ArgumentException("a sound has an id", nameof(sound));
        if (!float.IsFinite(at.X) || !float.IsFinite(at.Y) || !float.IsFinite(at.Z)) throw new ArgumentException("a place is finite numbers", nameof(at));
        if (!float.IsFinite(gainDb)) throw new ArgumentOutOfRangeException(nameof(gainDb), "a gain is a finite number of decibels");
        if (!(minDistance > 0f) || !float.IsFinite(minDistance)) throw new ArgumentOutOfRangeException(nameof(minDistance));
        if (!(range > 0f) || !float.IsFinite(range)) throw new ArgumentOutOfRangeException(nameof(range));
        return _hosted.Add(sound, loop, at, gainDb, minDistance, range, _now());
    }

    /// <summary>The played sounds that have not ended.</summary>
    internal int PlayedCount => _hosted.Count;

    /// <summary>Lets go of every played sound at once, in step with the world: nothing of them is left to place.</summary>
    private void EndHosted(bool forget)
    {
        var ids = new List<int>();
        _hosted.EndAll(ids);
        if (forget) foreach (int id in ids) ForgetEntity(id);
    }
}

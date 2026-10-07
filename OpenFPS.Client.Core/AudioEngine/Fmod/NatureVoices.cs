using System.Numerics;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Nature;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The voices for things nobody made: falling water, a fire, the wind in a tree. Ranked and budgeted
/// with the machines, and each reads the one <see cref="WindField"/> at its own fixed place, so a gust
/// that bends the trees upwind reaches the fire and the fountain a moment later.
/// </summary>
public abstract class NatureVoiceState : PhysicalVoiceState
{
    /// <summary>Map metres (x east, y up, z north).</summary>
    public readonly Vector3 Position;

    /// <summary>Metres above its base where it feels the wind: a tree's crown, the flames.</summary>
    protected readonly float WindHeight;

    protected NatureVoiceState(float sourceLevelDb, float headroomDb, float sampleRate, Vector3 position, float windHeight)
        : base(sourceLevelDb, sampleRate, headroomDb)
    {
        Position = position;
        WindHeight = windHeight;
    }

    /// <summary>m/s, read once a block against gusts that last seconds; each synth glides between the
    /// readings.</summary>
    protected float WindHere() => WindField.SpeedAt(Position.X, WindHeight, Position.Z, WindField.Now());

    protected override void PushListener(Vector3 frame) { }
}

/// <summary>A fountain, a cascade, a weir: water falling into water. See <see cref="FallingWaterSynth"/>.</summary>
public sealed class WaterVoiceState : NatureVoiceState
{
    public readonly FallingWaterSynth Water;

    public WaterVoiceState(WaterFeatureSpec spec, float sampleRate, int seed, Vector3 position)
        : base(spec.SourceLevelDb, spec.PeakHeadroomDb, sampleRate, position, spec.WindHeightMetres)
    {
        Water = new FallingWaterSynth(spec, sampleRate, seed);
    }

    protected override void Control(float seconds, float dt)
    {
        Water.Running = Running;
        Water.Wind = WindHere();
        Water.Control(dt);
    }

    protected override float StepSynth() => Water.Next();
}

/// <summary>
/// A water feature heard from several places at once: one <see cref="FallingWaterSynth"/>, each of its
/// taps (<see cref="WaterFeatureSpec.Taps"/>) a voice where that water lands, keyed
/// "water:&lt;preset&gt;/&lt;feature&gt;/&lt;tap&gt;".
///
/// One synth, so the pump, the wind in the spray and the jets' wandering are the fountain's, not each
/// tap's; each tap gets the events that land there, so a fountain eleven metres across is heard that
/// wide rather than from a point (two ears 0.92 alike at 2 m, docs/AUDIO_QUALITY_2026-10-06.md item 8).
/// Each tap renders against the whole feature's level and is placed by it
/// (ClientAudioSystem.LookUpPhysicalLevel), so past the feature the taps sum to it whatever the loudness
/// law's compression. A tap may itself be heard from several places across its landing
/// (ExtendedSources, FallingWaterSynth.NextPlaces); its middle carries its spread (<see cref="SetSpread"/>).
/// </summary>
public sealed class WaterFeatureVoice
{
    public readonly WaterFeatureSpec Spec;
    public readonly FallingWaterSynth Water;
    public readonly string Key;

    private const int RingBits = 17;
    private readonly float[][] _rings;
    private readonly int _mask = (1 << RingBits) - 1;
    private readonly float[] _taps;
    private readonly float[] _spread;
    public readonly int PlacesPerTap;
    private long _rendered;
    private readonly object _gate = new();
    private readonly float _rate;
    private int _untilControl;
    private const int ControlBlock = 256;

    /// <summary>The wind at the spray, as the latest tap read it: taps are metres apart, gusts tens of
    /// metres long.</summary>
    public volatile float Wind = float.NaN;
    public volatile bool Running = true;

    public int Taps;

    public WaterFeatureVoice(string key, WaterFeatureSpec spec, float sampleRate, int seed, int placesPerTap = 1)
    {
        Key = key;
        Spec = spec;
        _rate = sampleRate;
        Water = new FallingWaterSynth(spec, sampleRate, seed, placesPerTap);
        PlacesPerTap = Water.PlacesPerTap;
        _rings = new float[Water.TapCount * PlacesPerTap][];
        for (int i = 0; i < _rings.Length; i++) _rings[i] = new float[1 << RingBits];
        _taps = new float[_rings.Length];
        _spread = new float[Water.TapCount];
    }

    public long Newest => Volatile.Read(ref _rendered);

    /// <summary>Samples: a place further behind the newest render than this reads what has been
    /// written over.</summary>
    public const int RingLength = 1 << RingBits;

    /// <summary>How much of tap <paramref name="tap"/> its outer places carry, from its middle's emitter.</summary>
    public void SetSpread(int tap, float spread)
    {
        if (tap >= 0 && tap < _spread.Length) Volatile.Write(ref _spread[tap], spread);
    }

    /// <summary>Renders ahead under a lock on whichever render worker asks first.</summary>
    public float Sample(int tap, int place, long at)
    {
        if (at >= Volatile.Read(ref _rendered))
        {
            lock (_gate)
            {
                long have = _rendered;
                if (at >= have)
                {
                    long upto = at + 512;
                    for (long s = have; s < upto; s++)
                    {
                        if (--_untilControl <= 0)
                        {
                            _untilControl = ControlBlock;
                            float wind = Wind;
                            if (!float.IsNaN(wind)) Water.Wind = wind;
                            Water.Running = Running;
                            for (int t = 0; t < _spread.Length; t++) Water.SetSpread(t, Volatile.Read(ref _spread[t]));
                            Water.Control(ControlBlock / _rate);
                        }
                        Water.NextPlaces(_taps);
                        int idx = (int)(s & _mask);
                        for (int i = 0; i < _rings.Length; i++) _rings[i][idx] = _taps[i];
                    }
                    Volatile.Write(ref _rendered, upto);
                }
            }
        }
        int ring = Math.Clamp(tap, 0, Water.TapCount - 1) * PlacesPerTap + Math.Clamp(place, 0, PlacesPerTap - 1);
        return _rings[ring][(int)(at & _mask)];
    }

    /// <summary>"water:&lt;preset&gt;/&lt;feature&gt;/&lt;tap&gt;": one tap of one feature on a map. A plain
    /// "water:&lt;preset&gt;" is the whole feature at one point and is not a tap.</summary>
    public static bool ParseKey(string? soundId, out string preset, out string feature, out int tap)
    {
        preset = ""; feature = ""; tap = -1;
        if (soundId == null || !soundId.StartsWith("water:", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = soundId[6..].Split('/');
        if (parts.Length != 3) return false;
        preset = parts[0]; feature = parts[1];
        return int.TryParse(parts[2], out tap) && tap >= 0;
    }
}

/// <summary>One tap of a water feature, as a voice. See <see cref="WaterFeatureVoice"/>.</summary>
public sealed class WaterTapState : NatureVoiceState
{
    public readonly WaterFeatureVoice Shared;
    public readonly int Tap;
    /// <summary>0 the tap's middle (the map's emitter), the rest round it.</summary>
    public readonly int Place;
    private long _cursor;

    public WaterTapState(WaterFeatureVoice shared, int tap, float sampleRate, Vector3 position, int place = 0)
        : base(shared.Spec.SourceLevelDb, shared.Spec.PeakHeadroomDb, sampleRate, position, shared.Spec.WindHeightMetres)
    {
        Shared = shared;
        Tap = tap;
        Place = place;
        _cursor = shared.Newest;
        Interlocked.Increment(ref shared.Taps);
    }

    protected override void Control(float seconds, float dt)
    {
        Shared.Running = Running;
        Shared.Wind = WindHere();
    }

    protected override float StepSynth()
    {
        // As NaturePlaceState: a voice fallen further behind than the rings hold catches up.
        long newest = Shared.Newest;
        if (newest - _cursor > WaterFeatureVoice.RingLength - 4096) _cursor = newest;
        return Shared.Sample(Tap, Place, _cursor++);
    }
}

/// <summary>A wood fire. See <see cref="FireSynth"/>.</summary>
public sealed class FireVoiceState : NatureVoiceState
{
    public readonly FireSynth Fire;

    /// <summary>When it was lit on the shared clock (FireSpec.KeyFor), or null for one always burning.</summary>
    public double? LitAt;

    public FireVoiceState(FireSpec spec, float sampleRate, int seed, Vector3 position)
        : base(spec.SourceLevelDb, spec.PeakHeadroomDb, sampleRate, position, spec.FlameHeightMetres)
    {
        Fire = new FireSynth(spec, sampleRate, seed);
    }

    protected override void Control(float seconds, float dt)
    {
        Fire.Lit = Running;
        Fire.Wind = WindHere();
        if (LitAt is double lit) Fire.Age = WindField.Now() - lit;
        Fire.Control(dt);
    }

    protected override float StepSynth() => Fire.Next();
}

/// <summary>A tree, or a hedge, with the wind in it. See <see cref="FoliageSynth"/>.</summary>
public sealed class FoliageVoiceState : NatureVoiceState
{
    public readonly FoliageSynth Foliage;

    public FoliageVoiceState(FoliageSpec spec, float sampleRate, int seed, Vector3 position)
        : base(spec.SourceLevelDb, spec.PeakHeadroomDb, sampleRate, position, spec.CrownHeightMetres)
    {
        Foliage = new FoliageSynth(spec, sampleRate, seed);
    }

    protected override void Control(float seconds, float dt)
    {
        // Each bough reads the wind where it is in the crown, not all of them at its middle.
        Foliage.ReadWind(Position.X, Position.Z, WindField.Now());
        Foliage.Control(dt);
    }

    protected override float StepSynth() => Foliage.Next();
}

/// <summary>
/// A tree, a fire, running water or a shore heard from several places at once (ExtendedSources): one
/// synth rendering each place's own stream, each place a voice (<see cref="NaturePlaceState"/>). Place 0
/// is the map's emitter; the client adds the others while the source is wide enough at the listener.
/// The streams are independent, so each voice keeps its own cursor into the rings; the synth renders
/// ahead under a lock on whichever render worker asks first. <see cref="TargetSpread"/> comes already
/// slewed (ExtendedSources.Slew) and the synth glides to it, so nothing steps.
/// </summary>
public sealed class PlacedNatureVoice
{
    public readonly int Places;
    public readonly Vector3 Position;
    public readonly float SourceLevelDb, HeadroomDb, WindHeight;
    public readonly FoliageSynth? Foliage;
    public readonly FireSynth? Fire;
    public readonly RunningWaterSynth? Flow;
    public readonly ShoreSynth? Shore;

    private const int RingBits = 17;
    public const int RingLength = 1 << RingBits;
    private readonly float[][] _rings;
    private const int Mask = RingLength - 1;
    private readonly float[] _out;
    private long _rendered;
    private readonly object _gate = new();
    private readonly float _rate;
    private int _untilControl;
    private const int ControlBlock = 256;

    public volatile float TargetSpread;
    public volatile bool Running = true;
    /// <summary>For a wood heard as one (WoodChorus): the places its wind is read at, one a bough, and how
    /// many trees it stands for now (the client's, eased here so it never steps). Null for a tree.</summary>
    public Vector3[]? WindPlaces;
    public volatile float TargetTrees = 1f;
    private float _trees = -1f;
    /// <summary>Place voices reading this synth, for the log.</summary>
    public int Voices;

    public PlacedNatureVoice(string key, FoliageSpec spec, int places, float sampleRate, int seed, Vector3 position)
        : this(places, sampleRate, position, spec.SourceLevelDb, spec.PeakHeadroomDb, spec.CrownHeightMetres)
        => Foliage = new FoliageSynth(spec, sampleRate, seed, places);

    public PlacedNatureVoice(string key, FireSpec spec, int places, float sampleRate, int seed, Vector3 position)
        : this(places, sampleRate, position, spec.SourceLevelDb, spec.PeakHeadroomDb, spec.FlameHeightMetres)
    {
        Fire = new FireSynth(spec, sampleRate, seed, places);
        FireSpec.ParseKey(key, out _, out _litAt);
    }

    /// <summary>When a fire was lit on the shared clock (its key), or null for one always burning.</summary>
    private readonly double? _litAt;

    public PlacedNatureVoice(string key, RunningWaterSpec spec, float sampleRate, int seed, Vector3 position)
        : this(Math.Max(1, spec.Places), sampleRate, position, spec.SourceLevelDb, spec.PeakHeadroomDb, 1f)
        => Flow = new RunningWaterSynth(spec, sampleRate, seed);

    /// <summary>Waves at an edge (ShoreSynth); fetch, lie and length come from the map's key
    /// (ShoreSpec.KeyFor).</summary>
    public PlacedNatureVoice(string key, ShoreSpec spec, ShoreGeometry geometry, float sampleRate, int seed, Vector3 position)
        : this(spec.TotalPlaces, sampleRate, position, spec.SourceLevelDb, spec.PeakHeadroomDb, 10f)
        => Shore = new ShoreSynth(spec, sampleRate, seed, geometry);

    private PlacedNatureVoice(int places, float sampleRate, Vector3 position, float levelDb, float headroomDb, float windHeight)
    {
        Places = Math.Max(1, places);
        Position = position;
        SourceLevelDb = levelDb;
        HeadroomDb = headroomDb;
        WindHeight = windHeight;
        _rate = sampleRate;
        _rings = new float[Places][];
        for (int i = 0; i < Places; i++) _rings[i] = new float[RingLength];
        _out = new float[Places];
    }

    public long Newest => Volatile.Read(ref _rendered);

    /// <summary>Called from the render pool's worker threads; renders ahead as needed.</summary>
    public float Sample(int place, long at)
    {
        if (at >= Volatile.Read(ref _rendered))
        {
            lock (_gate)
            {
                long have = _rendered;
                if (at >= have)
                {
                    long upto = at + 512;
                    for (long s = have; s < upto; s++)
                    {
                        if (--_untilControl <= 0)
                        {
                            _untilControl = ControlBlock;
                            Control(ControlBlock / _rate);
                        }
                        if (Foliage != null) Foliage.NextPlaces(_out);
                        else if (Fire != null) Fire.NextPlaces(_out);
                        else if (Flow != null) Flow.NextPlaces(_out);
                        else Shore!.NextPlaces(_out);
                        int idx = (int)(s & Mask);
                        for (int i = 0; i < _rings.Length; i++) _rings[i][idx] = _out[i];
                    }
                    Volatile.Write(ref _rendered, upto);
                }
            }
        }
        return _rings[Math.Clamp(place, 0, _rings.Length - 1)][(int)(at & Mask)];
    }

    private void Control(float dt)
    {
        float spread = Math.Clamp(TargetSpread, 0f, 1f);
        double now = WindField.Now();
        if (Foliage != null)
        {
            Foliage.Spread = spread;
            if (WindPlaces != null)
            {
                // Eased over about a second: the share moves with the listener, a few metres a second.
                float target = MathF.Max(0f, TargetTrees);
                _trees = _trees < 0f ? target : _trees + (target - _trees) * MathF.Min(1f, dt / 0.5f);
                Foliage.Trees = _trees;
                Foliage.ReadWindAt(Position.X, Position.Z, now, WindPlaces);
            }
            else Foliage.ReadWind(Position.X, Position.Z, now);
            Foliage.Control(dt);
        }
        else if (Fire != null)
        {
            Fire.Spread = spread;
            Fire.Lit = Running;
            // The wind at each of its places: a gust crosses a big fire as it crosses a wood.
            Fire.ReadWind(Position.X, Position.Z, now);
            if (_litAt is double lit) Fire.Age = now - lit;
            Fire.Control(dt);
        }
        else if (Flow != null)
        {
            // Its water: its own flow, and the rain running off its catchment now (Runoff).
            var spec = Flow.Spec;
            Flow.Spread = spread;
            // A tap runs when somebody has turned it on (Running, from the server), and the basin under
            // it goes on draining after; anything else runs with its own flow and the rain.
            Flow.Flow = spec.Tap != null ? spec.FlowNow(tapOn: Running) : Running ? spec.FlowNow() : 0f;
            Flow.RainOnWater = Runoff.RainMmPerHour;
            Flow.Control(dt);
        }
        else if (Shore != null)
        {
            // The sea follows the mean wind over the water (the synth glides it over minutes); a gust is
            // too short to raise waves.
            var air = WindField.Weather.At(now);
            Shore.Spread = spread;
            Shore.WindSpeed = Running ? air.Speed : 0f;
            Shore.WindFromDegrees = air.FromDegrees;
            Shore.Control(dt);
        }
    }
}

/// <summary>One place of a <see cref="PlacedNatureVoice"/>, as a voice.</summary>
public sealed class NaturePlaceState : NatureVoiceState
{
    public readonly PlacedNatureVoice Shared;
    public readonly int Place;
    private long _cursor;

    /// <summary>Every place renders against the whole source's level and headroom, so the places'
    /// powers add up to the source's and the mixer places each by it.</summary>
    public NaturePlaceState(PlacedNatureVoice shared, int place, float sampleRate, Vector3 position)
        : base(shared.SourceLevelDb, shared.HeadroomDb, sampleRate, position, shared.WindHeight)
    {
        Shared = shared;
        Place = place;
        // Running water is a texture of events spread over its water: the ground hands back its power,
        // not a copy of it (GroundReflection.Texture).
        if (shared.Flow != null) Ground.Texture = true;
        _cursor = shared.Newest;
        Interlocked.Increment(ref shared.Voices);
    }

    protected override void Control(float seconds, float dt)
    {
        if (Place == 0) Shared.Running = Running;
    }

    protected override float StepSynth()
    {
        // A voice that fell further behind than the rings hold catches up rather than reading what has
        // been written over.
        long newest = Shared.Newest;
        if (newest - _cursor > PlacedNatureVoice.RingLength - 4096) _cursor = newest;
        return Shared.Sample(Place, _cursor++);
    }
}

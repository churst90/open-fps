using System;
using System.Numerics;
using System.Threading;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Nature;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// The voices for things nobody made: falling water, a fire, the wind in a tree. Each is a
/// <see cref="PhysicalVoiceState"/> like a machine, ranked and budgeted with the machines by what it
/// renders at the listener, and each reads the one <see cref="WindField"/> at its own place, so a
/// gust that bends the trees upwind of you reaches the fire and the fountain spray a moment later.
///
/// They need their POSITION, which a machine does not: a machine's state comes from its entity id,
/// but the wind at a tree is the wind where the tree is, and two trees fifty metres apart along the
/// wind hear the same gust ten seconds apart. The position is fixed when the voice is made — none of
/// these move.
/// </summary>
public abstract class NatureVoiceState : PhysicalVoiceState
{
    /// <summary>Where the source is, map metres (x east, y up, z north).</summary>
    public readonly Vector3 Position;

    /// <summary>How high above its base the wind it feels is, m: the crown of a tree, the flames.</summary>
    protected readonly float WindHeight;

    protected NatureVoiceState(float sourceLevelDb, float headroomDb, float sampleRate, Vector3 position, float windHeight)
        : base(sourceLevelDb, sampleRate, headroomDb)
    {
        Position = position;
        WindHeight = windHeight;
    }

    /// <summary>The wind where this source is, m/s, now. The field is read once a block, eleven
    /// milliseconds apart against gusts that last seconds; each synth glides between the readings.</summary>
    protected float WindHere() => WindField.SpeedAt(Position.X, WindHeight, Position.Z, WindField.Now());

    protected override void PushListener(Vector3 frame) { }
}

/// <summary>A fountain, a cascade, a weir: water falling into water. See <see cref="FallingWaterSynth"/>.</summary>
public sealed class WaterVoiceState : NatureVoiceState
{
    public readonly WaterFeatureSpec Spec;
    public readonly FallingWaterSynth Water;

    public WaterVoiceState(WaterFeatureSpec spec, float sampleRate, int seed, Vector3 position)
        : base(spec.SourceLevelDb, spec.PeakHeadroomDb, sampleRate, position, spec.WindHeightMetres)
    {
        Spec = spec;
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
/// A water feature heard from several places at once: ONE <see cref="FallingWaterSynth"/>, each of its
/// taps (<see cref="WaterFeatureSpec.Taps"/>) a voice of its own where that water lands on the map.
///
/// The map places one emitter per tap, keyed "water:&lt;preset&gt;/&lt;feature&gt;/&lt;tap&gt;"; every tap of
/// one feature reads this one synth, as a train's bogies read one TrainVoiceState (RailVoice.cs), so the
/// pump, the wind in the spray and the jets' wandering are the fountain's, not each tap's. Each tap
/// gets the events that land there, so the voices are as decorrelated as the water is, and a fountain
/// eleven metres across is heard as eleven metres across rather than from one point (two ears 0.92
/// alike at 2 m, docs/AUDIO_QUALITY_2026-10-06.md item 8).
///
/// Each tap voice renders against the WHOLE feature's level and headroom, and the mixer places each tap
/// by that level (ClientAudioSystem.LookUpPhysicalLevel): the taps' pressures are their own shares, so
/// at any distance past the feature they sum to the feature as one voice would play it, whatever the
/// loudness law's compression.
///
/// Each tap may itself be heard from several places across its landing (ExtendedSources): the tap's
/// map emitter is its middle, the client places the others, and every place reads its own stream of
/// the one synth (FallingWaterSynth.NextPlaces). The middle carries the tap's spread (<see cref="SetSpread"/>).
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

    /// <summary>The wind at the spray, as the most recent tap read it. Any tap's place will do: they are
    /// metres apart and gusts are tens of metres long.</summary>
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

    /// <summary>The rings' length, samples: a place further behind the newest render than this reads
    /// what has been written over.</summary>
    public const int RingLength = 1 << RingBits;

    /// <summary>How much of tap <paramref name="tap"/> its outer places carry, from its middle's emitter.</summary>
    public void SetSpread(int tap, float spread)
    {
        if (tap >= 0 && tap < _spread.Length) Volatile.Write(ref _spread[tap], spread);
    }

    /// <summary>Tap <paramref name="tap"/>'s sample at <paramref name="at"/>, rendering ahead as needed.
    /// Called from the render pool's worker threads.</summary>
    public float Sample(int tap, long at) => Sample(tap, 0, at);

    /// <summary>Place <paramref name="place"/> of tap <paramref name="tap"/> at <paramref name="at"/>.</summary>
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
    /// <summary>Which place of the tap: 0 its middle (the map's emitter), the rest round it.</summary>
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
    public readonly FireSpec Spec;
    public readonly FireSynth Fire;

    public FireVoiceState(FireSpec spec, float sampleRate, int seed, Vector3 position)
        : base(spec.SourceLevelDb, spec.PeakHeadroomDb, sampleRate, position, spec.FlameHeightMetres)
    {
        Spec = spec;
        Fire = new FireSynth(spec, sampleRate, seed);
    }

    protected override void Control(float seconds, float dt)
    {
        Fire.Lit = Running;
        Fire.Wind = WindHere();
        Fire.Control(dt);
    }

    protected override float StepSynth() => Fire.Next();
}

/// <summary>A tree, or a hedge, with the wind in it. See <see cref="FoliageSynth"/>.</summary>
public sealed class FoliageVoiceState : NatureVoiceState
{
    public readonly FoliageSpec Spec;
    public readonly FoliageSynth Foliage;

    public FoliageVoiceState(FoliageSpec spec, float sampleRate, int seed, Vector3 position)
        : base(spec.SourceLevelDb, spec.PeakHeadroomDb, sampleRate, position, spec.CrownHeightMetres)
    {
        Spec = spec;
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
/// A tree, a fire or running water heard from several places at once (ExtendedSources): ONE synth rendering each place's
/// own stream, each place a voice of its own (<see cref="NaturePlaceState"/>). Place 0 is the source's
/// middle, the map's own emitter; the others are made by the client when the source is wide enough at
/// the listener to be heard as wide, and let go when it is not.
///
/// The streams are independent (the synth writes each event to one place and gives each place its own
/// noise), so the voices do not need to read in step: each keeps its own cursor into the rings, as a
/// fountain's taps do. The synth renders ahead of the furthest cursor under a lock, on whichever render
/// worker asks first, and reads the wind where the source is.
///
/// <see cref="TargetSpread"/> is the client's, already slewed (ExtendedSources.Slew); it is handed to the
/// synth once a control block, and the synth glides its noise gains to it, so nothing steps.
/// </summary>
public sealed class PlacedNatureVoice
{
    public readonly string Key;
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
    /// <summary>How many place voices read this synth, for the log.</summary>
    public int Voices;

    public PlacedNatureVoice(string key, FoliageSpec spec, int places, float sampleRate, int seed, Vector3 position)
        : this(key, places, sampleRate, position, spec.SourceLevelDb, spec.PeakHeadroomDb, spec.CrownHeightMetres)
        => Foliage = new FoliageSynth(spec, sampleRate, seed, places);

    public PlacedNatureVoice(string key, FireSpec spec, int places, float sampleRate, int seed, Vector3 position)
        : this(key, places, sampleRate, position, spec.SourceLevelDb, spec.PeakHeadroomDb, spec.FlameHeightMetres)
        => Fire = new FireSynth(spec, sampleRate, seed, places);

    public PlacedNatureVoice(string key, RunningWaterSpec spec, float sampleRate, int seed, Vector3 position)
        : this(key, Math.Max(1, spec.Places), sampleRate, position, spec.SourceLevelDb, spec.PeakHeadroomDb, 1f)
        => Flow = new RunningWaterSynth(spec, sampleRate, seed);

    /// <summary>Waves at an edge (ShoreSynth): the source's own fetch, the way its water lies and its
    /// length come from the map's key (ShoreSpec.KeyFor).</summary>
    public PlacedNatureVoice(string key, ShoreSpec spec, ShoreGeometry geometry, float sampleRate, int seed, Vector3 position)
        : this(key, spec.TotalPlaces, sampleRate, position, spec.SourceLevelDb, spec.PeakHeadroomDb, 10f)
        => Shore = new ShoreSynth(spec, sampleRate, seed, geometry);

    private PlacedNatureVoice(string key, int places, float sampleRate, Vector3 position, float levelDb, float headroomDb, float windHeight)
    {
        Key = key;
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

    /// <summary>Place <paramref name="place"/>'s sample at <paramref name="at"/>, rendering ahead as
    /// needed. Called from the render pool's worker threads.</summary>
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
            Foliage.ReadWind(Position.X, Position.Z, now);
            Foliage.Control(dt);
        }
        else if (Fire != null)
        {
            Fire.Spread = spread;
            Fire.Lit = Running;
            Fire.Wind = WindField.SpeedAt(Position.X, WindHeight, Position.Z, now);
            Fire.Control(dt);
        }
        else if (Flow != null)
        {
            // Its water: its own flow, and the rain running off its catchment now (Runoff).
            var spec = Flow.Spec;
            Flow.Spread = spread;
            Flow.Flow = Running ? spec.FlowFor(Runoff.Through(spec.CatchmentSeconds)) : 0f;
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

/// <summary>One place of a tree or a fire, as a voice. See <see cref="PlacedNatureVoice"/>.</summary>
public sealed class NaturePlaceState : NatureVoiceState
{
    public readonly PlacedNatureVoice Shared;
    public readonly int Place;
    private long _cursor;

    /// <summary>Every place renders against the WHOLE source's level and headroom, so the places' powers
    /// at their shares add up to the source's, and the mixer places each by the source's level.</summary>
    public NaturePlaceState(PlacedNatureVoice shared, int place, float sampleRate, Vector3 position)
        : base(shared.SourceLevelDb, shared.HeadroomDb, sampleRate, position, shared.WindHeight)
    {
        Shared = shared;
        Place = place;
        _cursor = shared.Newest;
        Interlocked.Increment(ref shared.Voices);
    }

    protected override void Control(float seconds, float dt)
    {
        if (Place == 0) Shared.Running = Running;
    }

    protected override float StepSynth()
    {
        // A voice that fell further behind the newest render than the rings hold (it stopped being
        // produced while others went on) catches up rather than reading what has been written over.
        long newest = Shared.Newest;
        if (newest - _cursor > PlacedNatureVoice.RingLength - 4096) _cursor = newest;
        return Shared.Sample(Place, _cursor++);
    }
}

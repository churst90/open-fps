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

    public WaterFeatureVoice(string key, WaterFeatureSpec spec, float sampleRate, int seed)
    {
        Key = key;
        Spec = spec;
        _rate = sampleRate;
        Water = new FallingWaterSynth(spec, sampleRate, seed);
        _rings = new float[Water.TapCount][];
        for (int i = 0; i < _rings.Length; i++) _rings[i] = new float[1 << RingBits];
        _taps = new float[Water.TapCount];
    }

    public long Newest => Volatile.Read(ref _rendered);

    /// <summary>Tap <paramref name="tap"/>'s sample at <paramref name="at"/>, rendering ahead as needed.
    /// Called from the render pool's worker threads.</summary>
    public float Sample(int tap, long at)
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
                            Water.Control(ControlBlock / _rate);
                        }
                        Water.NextTaps(_taps);
                        int idx = (int)(s & _mask);
                        for (int i = 0; i < _rings.Length; i++) _rings[i][idx] = _taps[i];
                    }
                    Volatile.Write(ref _rendered, upto);
                }
            }
        }
        return _rings[Math.Clamp(tap, 0, _rings.Length - 1)][(int)(at & _mask)];
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
    private long _cursor;

    public WaterTapState(WaterFeatureVoice shared, int tap, float sampleRate, Vector3 position)
        : base(shared.Spec.SourceLevelDb, shared.Spec.PeakHeadroomDb, sampleRate, position, shared.Spec.WindHeightMetres)
    {
        Shared = shared;
        Tap = tap;
        _cursor = shared.Newest;
        Interlocked.Increment(ref shared.Taps);
    }

    protected override void Control(float seconds, float dt)
    {
        Shared.Running = Running;
        Shared.Wind = WindHere();
    }

    protected override float StepSynth() => Shared.Sample(Tap, _cursor++);
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

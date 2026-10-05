using System;
using System.Numerics;
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

    /// <summary>The wind where this source is, m/s, now. The field is read once a block, which is
    /// eleven milliseconds against gusts that last seconds.</summary>
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
        Foliage.Wind = WindHere();
        Foliage.Control(dt);
    }

    protected override float StepSynth() => Foliage.Next();
}

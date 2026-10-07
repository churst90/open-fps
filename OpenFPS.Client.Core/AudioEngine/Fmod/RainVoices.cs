using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;

namespace OpenFPS.Client.AudioEngine.Fmod;

/// <summary>
/// What the rain survey (RainField, game thread) and the rain voices (the render pool) share: one
/// slot per patch. The survey writes the patch and the rate; the voice writes back the level it is
/// measuring, which the survey places it by.
/// </summary>
public sealed class RainFeed
{
    /// <summary>The surfaces this voice renders. Replaced whole by the survey, never edited.</summary>
    public volatile RainPatch? Patch;

    private float _rate, _median, _hail;
    private int _kind;
    /// <summary>mm/h, the water-equivalent rate of whatever falls.</summary>
    public float Rate { get => Volatile.Read(ref _rate); set => Volatile.Write(ref _rate, value); }

    /// <summary>What falls: its kind, rate and sizes. Written field by field; a block that reads it
    /// mid-change renders one block of the old size at the new rate, which nobody can hear.</summary>
    public Precipitation Falling
    {
        get => new((PrecipitationKind)Volatile.Read(ref _kind), Volatile.Read(ref _rate), Volatile.Read(ref _median), Volatile.Read(ref _hail));
        set
        {
            Volatile.Write(ref _kind, (int)value.Kind);
            Volatile.Write(ref _median, value.MedianDropMm);
            Volatile.Write(ref _hail, value.HailMm);
            Volatile.Write(ref _rate, value.RateMmPerHour);
        }
    }

    private float _levelDb = float.NaN;
    /// <summary>The level the voice is rendering at, as dB at a metre of a source placed at the
    /// patch's reference distance: NaN until it has measured anything.</summary>
    public float LevelDb { get => Volatile.Read(ref _levelDb); set => Volatile.Write(ref _levelDb, value); }
}

/// <summary>The rain slots, and the keys their voices are made from ("rain:3").</summary>
public static class RainFeeds
{
    public const string KeyPrefix = "rain:";

    /// <summary>The overhead roof, and a near and a far patch in each of the four directions.</summary>
    public const int Slots = 9;

    public static readonly RainFeed[] Feed = MakeFeeds();

    private static RainFeed[] MakeFeeds()
    {
        var f = new RainFeed[Slots];
        for (int i = 0; i < Slots; i++) f[i] = new RainFeed();
        return f;
    }

    public static string Key(int slot) => KeyPrefix + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>"rain:0/2": part 2 of slot 0, the roof over the ear. Part 0 is plain "rain:0".</summary>
    public static string Key(int slot, int part) => part <= 0 ? Key(slot)
        : Key(slot) + "/" + part.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// How many voices a slot's patch is heard from (RainVoiceState, RainField.PlayParts); one each with
    /// ExtendedSources off.
    ///
    /// The roof over the ear, four, each a quarter of its area round the point over the ear: as one voice
    /// overhead both ears heard the same (interaural correlation 1.00 in a car and under a steel attic
    /// roof, 0.74-0.80 under the bus shelter; rain round 2, 2026-10-06). Each near quarter, two, 22.5°
    /// either side of its middle: from its middle, the north and south quarters were straight ahead and
    /// behind, half the near rain mono (the street 0.53 at 2 kHz, AudioLab --wide-sources). The far ring
    /// stays one a quarter: quieter, and further off the angles matter less.
    /// </summary>
    public static int PartsFor(int slot)
    {
        if (!OpenFPS.Client.AudioEngine.Core.Nature.ExtendedSources.Enabled) return 1;
        if (slot == OpenFPS.Client.Core.RainSurvey.OverheadSlot) return RoofParts;
        return slot >= 1 && slot <= 4 ? NearParts : 1;
    }

    public const int RoofParts = 4, NearParts = 2;

    public static bool TryParse(string key, out int slot) => TryParse(key, out slot, out _);

    public static bool TryParse(string key, out int slot, out int part)
    {
        slot = -1; part = 0;
        if (!key.StartsWith(KeyPrefix, StringComparison.Ordinal)) return false;
        var rest = key.AsSpan(KeyPrefix.Length);
        int slash = rest.IndexOf('/');
        if (slash >= 0)
        {
            if (!int.TryParse(rest[(slash + 1)..], System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out part) || part < 0) return false;
            rest = rest[..slash];
        }
        return int.TryParse(rest, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out slot)
               && slot >= 0 && slot < Slots;
    }
}

/// <summary>
/// One patch of rain as a voice (<see cref="RainSynth"/>; the patches come from RainField).
///
/// Rain has no one declared level, so the voice measures its own output over
/// <see cref="LevelSeconds"/>, renders at a fixed reference to sit in full scale like every physical
/// voice, and publishes the measured level; the survey places it by that level through Loudness.Place,
/// the law every source is placed by. A change faster than the measurement (a gust of heavier drops, a
/// near drop) goes straight through.
///
/// A part of a patch (RainFeeds.PartsFor) renders its share of every surface's area with its own drops
/// against the whole patch's level (its own level times the number of parts), so the parts together
/// play the patch. Only part 0 publishes the level.
/// </summary>
public sealed class RainVoiceState : PhysicalVoiceState
{
    /// <summary>dB at a metre: what the measured level is rendered at.</summary>
    public const float ReferenceDb = 60f;

    /// <summary>The room its peaks need over the measured level, dB: the 99.9th percentile of the 10 ms
    /// peaks over the Leq across the lab's scenes (--rain levels). Rain needs 15-30 dB, hail up to 43
    /// (golf balls on the street).</summary>
    public const float HeadroomDb = 45f;

    public const float LevelSeconds = 1.5f;

    private const float ReferencePascals = 20e-6f * 1000f;            // 60 dB
    /// <summary>30 dB as a mean square: under it the voice plays quieter than its reference rather than
    /// being lifted, so a few drops after a dry spell stay a few quiet drops.</summary>
    private const float FloorMeanSquare = 20e-6f * 20e-6f * 1000f;

    private readonly RainFeed _feed;
    public readonly RainSynth Synth;
    public readonly int Part, Parts;
    private RainPatch? _whole, _share;
    private double _meanSquare;
    private long _measured;
    private float _norm;
    private readonly float _alpha;

    public RainVoiceState(RainFeed feed, float sampleRate, int seed, int part = 0, int parts = 1)
        : base(ReferenceDb, sampleRate, HeadroomDb)
    {
        _feed = feed;
        Parts = Math.Max(1, parts);
        Part = Math.Clamp(part, 0, Parts - 1);
        // On the clock every rain voice shares, so the patches round a listener swell together.
        Synth = new RainSynth(sampleRate, seed) { Clock = OpenFPS.Common.WindField.Now() };
        _alpha = 1f / (LevelSeconds * sampleRate);
    }

    protected override void Control(float seconds, float dt)
    {
        var patch = _feed.Patch;
        if (Parts > 1 && patch != null)
        {
            if (!ReferenceEquals(patch, _whole)) { _whole = patch; _share = patch.Share(1f / Parts); }
            patch = _share;
        }
        Synth.Patch = patch;
        Synth.Falling = _feed.Falling;
        if (_measured > 0)
        {
            double ms = Math.Max(FloorMeanSquare, _meanSquare * Parts);
            _norm = (float)(ReferencePascals / Math.Sqrt(ms));
            if (Part == 0) _feed.LevelDb = (float)(10.0 * Math.Log10(ms / (20e-6 * 20e-6)));
        }
    }

    protected override float StepSynth()
    {
        float x = Synth.Next();
        // A running mean that starts as the plain mean of what there is, so the first second is not
        // measured against the silence before it.
        double a = Math.Max(_alpha, 1.0 / ++_measured);
        _meanSquare += (x * (double)x - _meanSquare) * a;
        return x * _norm;
    }

    protected override void PushListener(Vector3 frame) { }
}

using System;
using System.Numerics;
using System.Threading;
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
    /// <summary>The rain rate, mm/h (the water-equivalent rate of whatever falls).</summary>
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
    /// The roof over the ear, four. A roof over your head is rain landing all over it and a sheet ringing
    /// all over it, not a point: played as one voice straight overhead, both ears heard the same thing
    /// (interaural correlation 1.00 in a car and under a steel attic roof, 0.74-0.80 under the bus shelter,
    /// rain round 2, 2026-10-06). Each part renders a quarter of the roof's area from round the point over
    /// the ear.
    ///
    /// Each near quarter, two. A quarter played from its middle puts the whole of the north and south
    /// quarters straight ahead and behind, where both ears hear the same: half the near rain was mono, and
    /// the street measured 0.53 at 2 kHz (AudioLab --wide-sources). Two parts 22.5° either side of each
    /// quarter's middle. The far ring stays one a quarter: it is quieter, and further off the angles matter less.
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
/// One patch of rain as a voice. See <see cref="RainSynth"/> for the sound and RainField for where
/// the patches come from.
///
/// ITS LEVEL. Rain is not a source of one declared level: a patch is as loud as the rain is hard and
/// the surfaces in it are ringing, and both change. So the voice MEASURES what it renders — the mean
/// square of its own output over <see cref="LevelSeconds"/> — renders it at a fixed reference so it
/// sits in the mixer's full scale like every other physical voice, and publishes the measured level.
/// The survey places it by that level through Loudness.Place, the law every source in the game is
/// placed by, so light rain and a cloudburst stand in the same relation to a car going past as they
/// would in the street, compressed as everything is. A change faster than the measurement — a gust
/// of heavier drops, a near drop — goes straight through: only the slow level is handed to the law.
///
/// A PART of a patch (the roof over the ear, or a near quarter, heard from several places, RainFeeds.PartsFor) renders
/// its share of every surface's area with its own drops, and is rendered against the WHOLE patch's
/// level: its own measured level times the number of parts, the parts being alike. So each part plays
/// its share, the parts together play the patch, and all of them are placed by the patch's level.
/// Only part 0 publishes the level.
/// </summary>
public sealed class RainVoiceState : PhysicalVoiceState
{
    /// <summary>What the voice renders its measured level at, dB at a metre.</summary>
    public const float ReferenceDb = 60f;

    /// <summary>The room its peaks need over the measured level, dB. MEASURED with --rain levels:
    /// the 99.9th percentile of the 10 ms peaks over the Leq across the lab's scenes and kinds. Rain
    /// needs 15-30 dB; hail, whose stones are single blows on a quiet bed, up to 43 (golf balls on the
    /// street), and the patches render it too.</summary>
    public const float HeadroomDb = 45f;

    /// <summary>How long the level is measured over, s.</summary>
    public const float LevelSeconds = 1.5f;

    private const float ReferencePascals = 20e-6f * 1000f;            // 60 dB
    /// <summary>The quietest level the voice measures itself at, as a mean square (30 dB). Under it
    /// the voice is simply rendered quieter than its reference rather than lifted: a few drops after a
    /// dry spell are a few quiet drops, not a few drops at full scale.</summary>
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

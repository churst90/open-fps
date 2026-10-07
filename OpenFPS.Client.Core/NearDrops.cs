using System.Numerics;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Common;

namespace OpenFPS.Client.Core;

/// <summary>
/// The drops close enough to hear one by one, each where and when it lands.
///
/// <para>Further off, rain is a texture the patches (RainSynth) render. Within <see cref="NearRings"/>
/// of the survey (2.5 m), and on the listener's own head and shoulders under open sky
/// (RainSurfaces.HeadSquareMetres), the loudest drops are taken out of the patches and placed one at
/// a time, at a random point of their surface, with their own size and sound (DropBank).</para>
///
/// <para>Only up to <see cref="ResolvableImpactsPerSecond"/> on all near surfaces together: above
/// roughly ten to twenty a second, impacts stop being events and fuse into texture. In drizzle that
/// may be only the biggest of thousands; in hail, every stone.</para>
/// </summary>
public sealed class NearDrops
{
    /// <summary>Rings of the survey inside which drops are placed one by one: 2.5 m.</summary>
    public const int NearRings = 2;

    /// <summary>Separate impacts a second placed, on every near surface together. Measured with --rain
    /// resolve (Poisson drops on asphalt, a puddle, a head and 0.7 mm steel, counted by an onset
    /// detector: a 1 ms peak 12 dB over the median of the 200 ms round it, none within 30 ms of
    /// another): the count follows the true rate within a fifth up to 12 a second (16 on steel) and
    /// never passes about 27 however many fall.</summary>
    public const float ResolvableImpactsPerSecond = 12f;

    /// <summary>One impact: where, on what, what, how big, how fast, when (seconds), whether it strikes
    /// from below the ear, its patch slot, and whether it is a hailstone coming down from its bounce.</summary>
    public readonly record struct Impact(Vector3 Position, RainLayer Surface, PrecipitationKind Kind, float DiameterMm,
                                         float Speed, double At, bool FromBelow, int Slot, bool Bounce);

    private readonly Random _rng;
    private readonly ParticleSpectrum _main = new(), _stones = new();
    private readonly List<Impact> _bounces = new();

    /// <summary>The smallest near drop placed one by one, mm, for the rain and for hail.</summary>
    public float RainFromMm { get; private set; } = float.MaxValue;
    public float HailFromMm { get; private set; } = float.MaxValue;

    public NearDrops(int seed) { _rng = new Random(seed); }

    /// <summary>
    /// The impacts in [<paramref name="t"/>, t + <paramref name="dt"/>) on the survey's near cells, and
    /// the patches told which drops are no longer theirs.
    /// </summary>
    public void Plan(RainSurvey.Result survey, Precipitation falling, Vector3 ear, double t, float dt, List<Impact> into, DropBank bank)
    {
        // Bounces due now.
        for (int i = _bounces.Count - 1; i >= 0; i--)
            if (_bounces[i].At < t + dt) { into.Add(_bounces[i]); _bounces.RemoveAt(i); }
        if (!falling.Falling) return;
        if (!ReferenceEquals(survey, _for) || falling != _forFalling || Vector3.DistanceSquared(ear, _forEar) > 0.25f)
            Allocate(survey, falling, ear, bank);
        var mainKind = falling.Kind == PrecipitationKind.Hail ? PrecipitationKind.Rain : falling.Kind;
        for (int i = 0; i < survey.Near.Count; i++)
        {
            var c = survey.Near[i];
            if (c.Slot < 0 || survey.Patches[c.Slot] == null) continue;
            Cell(c, _main, mainKind, _rainFrom[i], ear, t, dt, into);
            if (falling.Kind == PrecipitationKind.Hail) Cell(c, _stones, PrecipitationKind.Hail, _hailFrom[i], ear, t, dt, into);
        }
    }

    private RainSurvey.Result? _for;
    private Precipitation _forFalling;
    private Vector3 _forEar;
    private float[] _rainFrom = Array.Empty<float>(), _hailFrom = Array.Empty<float>(), _gain = Array.Empty<float>();

    /// <summary>
    /// Which drops each near cell places one by one: the loudest at the ear, up to the budget. A drop's
    /// peak at the ear is a reference drop rendered on its surface (DropBank), over distance, by how
    /// squarely the surface faces the ear, growing as (D v)^1.5. One level is found such that the drops
    /// over it on all cells come to <see cref="ResolvableImpactsPerSecond"/>; each cell's threshold is
    /// the size reaching that level there. So the sheet over your head gives its drops first, and the
    /// road two metres down only its biggest.
    /// </summary>
    private void Allocate(RainSurvey.Result survey, Precipitation falling, Vector3 ear, DropBank bank)
    {
        _for = survey; _forFalling = falling; _forEar = ear;
        int n = survey.Near.Count;
        if (_rainFrom.Length < n) { _rainFrom = new float[n]; _hailFrom = new float[n]; _gain = new float[n]; }
        Array.Fill(_rainFrom, float.MaxValue); Array.Fill(_hailFrom, float.MaxValue);
        foreach (var c in survey.Near)
        {
            Array.Fill(c.Surface.DiscreteRainFromMm, float.MaxValue);
            Array.Fill(c.Surface.DiscreteHailFromMm, float.MaxValue);
        }
        RainFromMm = HailFromMm = float.MaxValue;

        var mainKind = falling.Kind == PrecipitationKind.Hail ? PrecipitationKind.Rain : falling.Kind;
        _main.Build(mainKind, falling.RateMmPerHour,
                    falling.Kind == PrecipitationKind.Hail ? Hydrometeors.MarshallPalmerMedianMm(falling.RateMmPerHour) : falling.EffectiveMedianMm);
        float budget = ResolvableImpactsPerSecond;
        if (falling.Kind == PrecipitationKind.Hail)
        {
            _stones.Build(falling);
            float placed = Share(survey, _stones, PrecipitationKind.Hail, MathF.Max(5f, _stones.MedianMm), budget, ear, bank, _hailFrom);
            HailFromMm = Min(_hailFrom, n);
            budget = MathF.Max(2f, budget - placed);
        }
        Share(survey, _main, mainKind, ReferenceDropMm(mainKind), budget, ear, bank, _rainFrom);
        RainFromMm = Min(_rainFrom, n);

        // The patches render what is left of each near bin.
        for (int i = 0; i < n; i++)
        {
            var c = survey.Near[i];
            int bin = c.Surface.BinOfRing(c.Ring);
            if (bin < 0) continue;
            c.Surface.DiscreteRainFromMm[bin] = MathF.Min(c.Surface.DiscreteRainFromMm[bin], _rainFrom[i]);
            c.Surface.DiscreteHailFromMm[bin] = MathF.Min(c.Surface.DiscreteHailFromMm[bin], _hailFrom[i]);
        }
    }

    private static float ReferenceDropMm(PrecipitationKind kind) => kind switch
    {
        PrecipitationKind.Sleet => Hydrometeors.SleetMedianMm,
        PrecipitationKind.Snow => 1.5f,
        _ => 2.5f,
    };

    private static float Min(float[] a, int n) { float m = float.MaxValue; for (int i = 0; i < n; i++) m = MathF.Min(m, a[i]); return m; }

    /// <summary>Sets each cell's threshold so the loudest <paramref name="budget"/> a second are placed;
    /// returns how many a second that is.</summary>
    private float Share(RainSurvey.Result survey, ParticleSpectrum s, PrecipitationKind kind, float referenceMm, float budget,
                        Vector3 ear, DropBank bank, float[] from)
    {
        int n = survey.Near.Count;
        float vRef = Hydrometeors.FallSpeed(kind, referenceMm);
        float best = 0f;
        for (int i = 0; i < n; i++)
        {
            var c = survey.Near[i];
            _gain[i] = 0f;
            if (c.Slot < 0 || survey.Patches[c.Slot] == null) continue;
            float rc = MathF.Sqrt(0.5f * (c.Inner * c.Inner + c.Outer * c.Outer));
            var at = new Vector3(ear.X + rc * MathF.Cos(c.Angle), c.Top, ear.Z + rc * MathF.Sin(c.Angle));
            var probe = new Impact(at, c.Surface, kind, referenceMm, vRef, 0, c.FromBelow, c.Slot, false);
            var sound = bank.Get(probe, 0);
            if (sound == null) continue;
            float dist = MathF.Max(0.1f, Vector3.Distance(at, ear));
            float level = MathF.Pow(10f, DropBank.LevelDb(sound, probe, (ear.Y - c.Top) / dist) / 20f) / dist;
            _gain[i] = level;
            best = MathF.Max(best, level);
        }
        if (best <= 0f || s.PerSquareMetreSecond <= 0f) return 0f;
        // The level threshold, by bisection in log: the count over it falls as it rises.
        float lo = best * 1e-4f, hi = best * 1e3f;
        for (int it = 0; it < 40; it++)
        {
            float mid = MathF.Sqrt(lo * hi);
            if (Count(survey, s, kind, referenceMm, vRef, mid, null) > budget) lo = mid; else hi = mid;
        }
        return Count(survey, s, kind, referenceMm, vRef, hi, from);
    }

    /// <summary>How many a second land over this level at the ear; with <paramref name="from"/>, each
    /// cell's size threshold for it.</summary>
    private float Count(RainSurvey.Result survey, ParticleSpectrum s, PrecipitationKind kind, float referenceMm, float vRef,
                        float level, float[]? from)
    {
        float total = 0f;
        for (int i = 0; i < survey.Near.Count; i++)
        {
            if (_gain[i] <= 0f) continue;
            // The size whose (D v)^1.5 lifts the reference drop's level here to the threshold.
            float target = referenceMm * vRef * MathF.Pow(level / _gain[i], 2f / 3f);
            float dlo = 0.05f, dhi = 200f;
            for (int it = 0; it < 30; it++)
            {
                float mid = MathF.Sqrt(dlo * dhi);
                if (mid * Hydrometeors.FallSpeed(kind, mid) < target) dlo = mid; else dhi = mid;
            }
            float threshold = dhi;
            total += s.PerSquareMetreSecond * s.ShareAbove(threshold) * survey.Near[i].Area;
            if (from != null) from[i] = threshold;
        }
        return total;
    }

    private void Cell(RainSurvey.NearCell c, ParticleSpectrum s, PrecipitationKind kind, float from, Vector3 ear, double t, float dt, List<Impact> into)
    {
        // Clustered as the patches are (RainSynth.Intermittency): the big drops come in the same swells.
        float mean = s.PerSquareMetreSecond * s.ShareAbove(from) * c.Area * dt * OpenFPS.Client.AudioEngine.Core.Nature.RainSynth.Intermittency(t);
        int n = Poisson(mean);
        for (int k = 0; k < n; k++)
        {
            float d = s.DrawAbove(from, (float)_rng.NextDouble());
            float v = Hydrometeors.FallSpeed(kind, d);
            // Somewhere in the cell: uniform over its area.
            float r = MathF.Sqrt(c.Inner * c.Inner + (float)_rng.NextDouble() * (c.Outer * c.Outer - c.Inner * c.Inner));
            float a = c.Angle + ((float)_rng.NextDouble() - 0.5f) * c.Width;
            var at = new Vector3(ear.X + r * MathF.Cos(a), c.Top, ear.Z + r * MathF.Sin(a));
            double when = t + _rng.NextDouble() * dt;
            into.Add(new Impact(at, c.Surface, kind, d, v, when, c.FromBelow, c.Slot, false));
            // A hailstone or a pellet bounces off anything hard and comes down again (Hydrometeors.Restitution).
            if (Hydrometeors.IsIce(kind) && c.Surface.Kind is RainSurfaceKind.Hard or RainSurfaceKind.Plate)
            {
                float e = Hydrometeors.Restitution(c.Surface.ModulusGPa);
                float up = e * v;
                double airborne = 2.0 * up / 9.81;
                if (up > 0.5f && airborne < 3.0)
                {
                    // It lands a short way off: up to 5 cm for every metre a second it rebounds with.
                    float drift = 0.05f * up * (float)_rng.NextDouble();
                    float dir = (float)(_rng.NextDouble() * MathF.Tau);
                    var land = at + new Vector3(drift * MathF.Cos(dir), 0f, drift * MathF.Sin(dir));
                    _bounces.Add(new Impact(land, c.Surface, kind, d, up, when + airborne, c.FromBelow, c.Slot, true));
                }
            }
        }
    }

    private int Poisson(float mean)
    {
        if (mean <= 0f) return 0;
        if (mean > 30f) return Math.Max(0, (int)MathF.Round(mean + MathF.Sqrt(mean) * Gauss()));
        double limit = Math.Exp(-mean), p = 1.0;
        int k = 0;
        do { k++; p *= _rng.NextDouble(); } while (p > limit);
        return k - 1;
    }

    private float Gauss()
        => MathF.Sqrt(-2f * MathF.Log(MathF.Max(1e-7f, (float)_rng.NextDouble()))) * MathF.Cos(MathF.Tau * (float)_rng.NextDouble());
}

/// <summary>
/// The sounds of single drops, rendered by the rain's own synthesiser (RainSynth.RenderOne) for each
/// surface, kind and size, a quarter of an octave of size apart, three variants each, as they are first
/// needed: a one-shot at a metre, normalised to its peak, with that peak's level. Nothing recorded.
/// </summary>
public sealed class DropBank
{
    public const int Rate = 48000;
    public const int Variants = 3;
    private const float StepsPerOctave = 4f;

    public sealed class Sound
    {
        public required string Id;
        /// <summary>The waveform, peak 1.</summary>
        public required float[] Pcm;
        /// <summary>Its peak, Pa at a metre, and the size it was rendered for, mm.</summary>
        public required float PeakPascals;
        public required float DiameterMm;
    }

    private readonly Dictionary<string, Sound> _made = new(StringComparer.Ordinal);
    private int _seed = 1;

    /// <summary>Every sound made so far.</summary>
    public IEnumerable<Sound> Made => _made.Values;

    /// <summary>The key a drop's sound is filed under: what it lands on, what it is, its size step, a variant.</summary>
    public static string Key(RainLayer surface, PrecipitationKind kind, float diameterMm, int variant)
        => $"raindrop:{surface.Key}|{kind}|{Step(diameterMm)}|{variant}";

    private static int Step(float d) => (int)MathF.Round(MathF.Log2(MathF.Max(0.05f, d)) * StepsPerOctave);

    /// <summary>The key's seed, the same in every process (FNV-1a). string.GetHashCode is randomised per
    /// process, which made every client, and every run of a test, render different drops.</summary>
    private static int StableHash(string s)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (char c in s) h = (h ^ c) * 16777619;
            return (int)h;
        }
    }

    /// <summary>The sound for an impact, made if it is not yet, unless <paramref name="mayMake"/> is false.</summary>
    public Sound? Get(in NearDrops.Impact impact, int variant, bool mayMake = true)
    {
        string key = Key(impact.Surface, impact.Kind, impact.DiameterMm, variant);
        if (_made.TryGetValue(key, out var s)) return s;
        if (!mayMake) return null;
        float d = MathF.Pow(2f, Step(impact.DiameterMm) / StepsPerOctave);
        float v = impact.Bounce ? impact.Speed : Hydrometeors.FallSpeed(impact.Kind, d);
        var surface = impact.Surface;
        bool rings = surface.Kind == RainSurfaceKind.Plate || (surface.Kind == RainSurfaceKind.Pool && impact.Kind is PrecipitationKind.Rain or PrecipitationKind.FreezingRain or PrecipitationKind.Hail);
        int samples = (int)((rings ? 0.35f : 0.06f) * Rate);
        var synth = new RainSynth(Rate, unchecked(StableHash(key) * 31 + _seed++));
        var pcm = synth.RenderOne(surface, impact.Kind, d, v, samples);
        float peak = 0f;
        foreach (float x in pcm) peak = MathF.Max(peak, MathF.Abs(x));
        if (peak > 0f) for (int i = 0; i < pcm.Length; i++) pcm[i] /= peak;
        // Fade the last few milliseconds, so a ring cut short does not click.
        int fade = Math.Min(pcm.Length, Rate / 200);
        for (int i = 0; i < fade; i++) pcm[pcm.Length - 1 - i] *= i / (float)fade;
        s = new Sound { Id = key, Pcm = pcm, PeakPascals = peak, DiameterMm = d };
        _made[key] = s;
        return s;
    }

    /// <summary>
    /// The volume and reference distance a near drop plays at, in the loudness frame of the rain it
    /// belongs to: placed by the patch's level (<paramref name="fieldLevelDb"/>, measured by
    /// RainVoiceState), a drop 9 dB under it arrives 9 dB under. Placed by its own peak, each drop was
    /// lifted by the law's compression, and under a steel shelter heavy rain sounded like a few metallic
    /// drops (Cody, 2026-10-06; physically they are 5 dB under at light rain, 10 at heavy). With no
    /// measured field (NaN) the drop is placed by its own level.
    ///
    /// A drop's level is its peak (its sound is normalised to it); a patch's is its Leq, rendered the
    /// fleet's shared headroom (VehicleProfile.PeakHeadroomDb) under full scale, hence the headroom here.
    /// </summary>
    public static (float Gain, float Reference) Placement(float dropLevelDb, float fieldLevelDb)
    {
        if (float.IsNaN(fieldLevelDb) || fieldLevelDb <= 0f) return Loudness.Place(dropLevelDb);
        var (gain, reference) = Loudness.Place(fieldLevelDb);
        float relative = dropLevelDb - fieldLevelDb - VehicleProfile.PeakHeadroomDb;
        return (MathF.Min(1f, gain * MathF.Pow(10f, relative / 20f)), reference);
    }

    /// <summary>
    /// The peak level at a metre an impact plays at, dB SPL: its sound's own, scaled from the size it
    /// was rendered for as (D v)^1.5 (for a bounce, at the bounce's speed), and by how squarely it faces
    /// the ear (<paramref name="aim"/>, the dipole's cosine; a canopy or a strike from below counts as
    /// square on).
    /// </summary>
    public static float LevelDb(Sound s, in NearDrops.Impact impact, float aim)
    {
        float d = impact.DiameterMm, v = impact.Speed;
        float vRef = impact.Bounce ? v : Hydrometeors.FallSpeed(impact.Kind, s.DiameterMm);
        float scale = MathF.Pow(d * v / MathF.Max(1e-3f, s.DiameterMm * vRef), 1.5f);
        float facing = impact.Surface.Kind == RainSurfaceKind.Canopy || impact.FromBelow ? 1f : Math.Clamp(aim, RainLayer.MinAim, 1f);
        return 20f * MathF.Log10(MathF.Max(1e-9f, s.PeakPascals * scale * facing) / 20e-6f);
    }
}

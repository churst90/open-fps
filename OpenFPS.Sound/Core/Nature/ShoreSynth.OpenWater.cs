using System.Numerics;
using OpenFPS.Environment;
using OpenFPS.Physics;

namespace OpenFPS.Sound.Core.Nature;

// Whitecaps over open water (docs/RUNNING_WATER.md 15.11, 2026-10-17): a pond's or a lake's surface heard from the whole of
// itself, cut at the ear (ExtendedSources.CutArea), each piece playing the crests that break in it. Nothing in it steps
// (Cody: "make sure the sound doesn't step/cut on/cut off"): the sea glides as a sea grows, each point's gust glides as the
// wind field does, each piece's breaking glides, every break and every crowd of them rises and falls over its own envelope.

public sealed partial class ShoreSynth
{
    /// <summary>The wind over the water and the shared clock, for the gusts crossing it (WindField): set by the voice.
    /// Without it the water feels the mean wind everywhere.</summary>
    public WindWeather? Gusts;
    public double Now;

    /// <summary>Each point's wind is read from the field this often, s, and glides to it over <see cref="GustGlideSeconds"/>:
    /// the field's shortest eddy passes in about a second, so the glide follows it without a step.</summary>
    private const float GustSeconds = 0.25f, GustGlideSeconds = 0.25f;

    /// <summary>How fast a piece's breaking follows what its points say, s: a cut moved on, a turn of the wind or a gust
    /// changes how many crests break there over about this, never at once.</summary>
    private const float BreakingGlideSeconds = 0.5f;

    /// <summary>A piece whose crests break oftener than this in <see cref="CrowdSeconds"/> is a crowd: its breaks are
    /// gathered into one cloud of their air each <see cref="CrowdSeconds"/> rather than one each, which a hectare in a
    /// gale (thousands a second) would need hundreds of processes for. Each crowd's cloud lasts a crest's half period
    /// longer than the interval, so they overlap and rise and fall into each other.</summary>
    private const float CrowdBreaks = 4f;
    private const float CrowdSeconds = 0.25f;

    /// <summary>What a piece of open water holds: its points (from the cut), the wind at each, and its crests breaking.</summary>
    private sealed class Patch
    {
        public int Count;
        public Vector2[] At = Array.Empty<Vector2>();
        public float[] Area = Array.Empty<float>(), Fetch = Array.Empty<float>(), Gust = Array.Empty<float>(), GustTarget = Array.Empty<float>();
        /// <summary>Breaks a second over the piece now, the height and period of those that break (their mean) and the air
        /// they fold under (m³/s): each gliding to what its points say.</summary>
        public float Rate, Height, Period = 1f, Air;
        /// <summary>For the lab: the area with breaking crests and the mean fetch.</summary>
        public float Breaking, MeanFetch;
        /// <summary>For the lab: the wind over it now, its points' gusts on the mean, m/s.</summary>
        public float Wind;
        public float Crowd;
    }

    private Patch[]? _patches;
    private float _fetchFrom = float.NaN, _gustClock;
    /// <summary>The wind the sea has been raised by: it follows the mean wind over the time a sea takes to grow (a third
    /// of the CEM's duration for the water's mean fetch, never faster than ten seconds), so the sea grows and dies.</summary>
    private float _seaWind = float.NaN;

    /// <summary>For the lab and the tests: each piece of open water now, its breaks a second, the significant height of
    /// its sea where it breaks, the area breaking (m²), its mean fetch (m) and the air its breaks fold under (m³/s).</summary>
    public IReadOnlyList<(float Rate, float Hs, float Breaking, float Fetch, float Air, float Wind)> OpenColumns
        => (_patches ?? Array.Empty<Patch>()).Select(p => (p.Rate, p.Height, p.Breaking, p.MeanFetch, p.Air, p.Wind)).ToList();

    /// <summary>
    /// The sea over each piece of the surface: at each of its points the fetch (how far the water reaches upwind of it),
    /// the fetch-limited sea the wind raises there (JONSWAP, as an edge's), and how many of its dominant crests break in
    /// the wind there now, gust or lull (WindWaves.GustBreaking). A young sea is short-crested: about one crest a
    /// wavelength across and a wavelength apart passes a point each period, so b / (L² T) break a second on each square
    /// metre. A crest too slow to fold air under breaks without a sound (microbreaking) and is no whitecap; the air of the
    /// rest grows from nothing with its crest's speed (Entrains). The piece's breaks are its points' together.
    /// </summary>
    private void AreaControl(float dt)
    {
        if (!ReferenceEquals(Cut, _cutBy)) AreaRecut(Cut);
        _toHome = 1f;
        float wind = float.IsFinite(WindSpeed) ? MathF.Max(0f, WindSpeed) : 0f;
        // The fetch of every point again when the wind has turned two degrees: the sea it raises changes a few per cent,
        // and each piece glides there.
        bool refetch = !(MathF.Abs(DegreesApart(WindFromDegrees, _fetchFrom)) < 2f);
        if (refetch) _fetchFrom = WindFromDegrees;
        _gustClock -= dt;
        bool read = _gustClock <= 0f;
        if (read) _gustClock = GustSeconds;
        float from = _fetchFrom * (MathF.PI / 180f);
        var upwind = new Vector2(MathF.Sin(from), MathF.Cos(from));
        float glideGust = MathF.Min(1f, dt / GustGlideSeconds), glideBreak = MathF.Min(1f, dt / BreakingGlideSeconds);

        // The sea's wind, over the water's mean fetch.
        double fetchAll = 0, areaAll = 0;
        foreach (var p in _patches ?? Array.Empty<Patch>())
            for (int k = 0; k < p.Count; k++)
            {
                if (refetch || p.Fetch[k] < 0f) p.Fetch[k] = Cut?.Path is { } path ? Across(path, p.At[k], upwind) ?? 0f : 0f;
                fetchAll += p.Fetch[k] * (double)p.Area[k];
                areaAll += p.Area[k];
            }
        float meanFetch = areaAll > 0 ? (float)(fetchAll / areaAll) : Spec.FetchMetres;
        if (float.IsNaN(_seaWind)) _seaWind = wind;
        else
        {
            float tau = MathF.Max(10f, WindWaves.GrowthSeconds(MathF.Max(wind, 2f), MathF.Max(1f, meanFetch)) / 3f);
            _seaWind += (wind - _seaWind) * MathF.Min(1f, dt / tau);
        }

        float most = 0f, mostTp = 1f;
        foreach (var p in _patches ?? Array.Empty<Patch>())
        {
            if (p.Count == 0) { Glide(p, 0, 0, 1, 0, glideBreak); p.Breaking = 0f; continue; }
            double rate = 0, height = 0, period = 0, breaking = 0, fetchSum = 0, area = 0, air = 0, windSum = 0;
            for (int k = 0; k < p.Count; k++)
            {
                if (read || float.IsNaN(p.GustTarget[k]))
                    p.GustTarget[k] = Gusts != null ? WindField.SpeedAt(Gusts, p.At[k].X, 10f, p.At[k].Y, Now) : wind;
                p.Gust[k] = float.IsNaN(p.Gust[k]) ? p.GustTarget[k] : p.Gust[k] + (p.GustTarget[k] - p.Gust[k]) * glideGust;
                fetchSum += p.Fetch[k] * (double)p.Area[k];
                windSum += p.Gust[k] * (double)p.Area[k];
                area += p.Area[k];
                if (p.Fetch[k] <= 0f) continue;
                var (hs, tp) = WindWaves.FetchLimited(_seaWind, p.Fetch[k], Spec.DepthMetres);
                float b = WindWaves.GustBreaking(hs, tp, wind, p.Gust[k]);
                if (!(b > 0f)) continue;
                float crestSpeed = WindWaves.Gravity * tp / (2f * MathF.PI);
                float entrains = Entrains(crestSpeed);
                if (!(entrains > 0f)) continue;
                float l = WindWaves.DeepWavelength(tp);
                double r = b / (l * (double)l * tp) * p.Area[k];
                rate += r;
                height += r * hs;
                period += r * tp;
                breaking += p.Area[k];
                // Each folds SpillingAirShare of a plunger's air under along a third of its wavelength: 0.84 Hs² on the mean.
                air += r * SpillingAirShare * AirPerBreak * 0.84 * hs * hs * 0.3 * l * entrains;
                if (hs > most) { most = hs; mostTp = tp; }
            }
            Glide(p, (float)rate, rate > 0 ? (float)(height / rate) : p.Height, rate > 0 ? (float)(period / rate) : p.Period, (float)air, glideBreak);
            p.Breaking = (float)breaking;
            p.MeanFetch = area > 0 ? (float)(fetchSum / area) : 0f;
            p.Wind = area > 0 ? (float)(windSum / area) : wind;
        }
        _hs = most;
        _tp = mostTp;

        static void Glide(Patch p, float rate, float height, float period, float air, float a)
        {
            p.Rate += (rate - p.Rate) * a;
            p.Air += (air - p.Air) * a;
            // Height and period are the breaking crests': held while none break, so a piece starting again starts from them.
            if (rate > 0f || p.Rate > 0f)
            {
                p.Height = p.Height <= 0f ? height : p.Height + (height - p.Height) * a;
                p.Period += (period - p.Period) * a;
            }
        }
    }

    /// <summary>Each piece's points from a new cut (AreaPieces). A piece's breaking glides from what it had: a recut is the
    /// ear moving, not the water changing.</summary>
    private void AreaRecut(ExtendedSources.LineCut? cut)
    {
        _cutBy = cut;
        _patches ??= Enumerable.Range(0, _along).Select(_ => new Patch()).ToArray();
        var area = cut?.Area;
        for (int j = 0; j < _patches.Length; j++)
        {
            var p = _patches[j];
            p.Count = 0;
            if (area == null || j >= cut!.Count) continue;
            int i = cut.PieceOfPlace(j);
            int from = area.SampleStart[i], n = area.SampleStart[i + 1] - from;
            if (p.At.Length < n)
            {
                p.At = new Vector2[n]; p.Area = new float[n]; p.Fetch = new float[n]; p.Gust = new float[n]; p.GustTarget = new float[n];
            }
            for (int k = 0; k < n; k++)
            {
                p.At[k] = area.Samples[from + k];
                p.Area[k] = area.SampleArea[from + k];
                p.Fetch[k] = -1f;
                p.Gust[k] = p.GustTarget[k] = float.NaN;
            }
            p.Count = n;
        }
    }

    /// <summary>
    /// The crests breaking over each piece this block, each folding air under as a spilling crest does (as the whitecaps
    /// in front of an edge always have: SpillingAirShare of a plunger's air along a third of its wavelength, Entrains of
    /// it by its crest's speed), heard from its shell (PlumeHeard), each a cloud that rises and falls over its own half
    /// period. A piece breaking oftener than a few times a quarter second is a crowd: one cloud of all its breaks' air
    /// each quarter second, the clouds overlapping.
    /// </summary>
    private void OpenWater(float dt)
    {
        if (_patches == null || WhitecapPart <= 0f) return;
        for (int j = 0; j < _patches.Length; j++)
        {
            var p = _patches[j];
            if (!(p.Rate > 1e-6f)) { p.Crowd = 0f; continue; }
            float t = MathF.Max(0.1f, p.Period);
            if (p.Rate * CrowdSeconds < CrowdBreaks)
            {
                int n = _rng.Poisson(p.Rate * dt);
                for (int i = 0; i < n; i++)
                {
                    float hb = p.Height * (0.6f + 0.6f * _rng.Uniform());
                    float period = t * (0.85f + 0.3f * _rng.Uniform());
                    float l = WindWaves.DeepWavelength(period);
                    float air = SpillingAirShare * AirPerBreak * hb * hb * (0.3f * l) * Entrains(WindWaves.Gravity * period / (2f * MathF.PI));
                    StartCloud(j, (int)(_rng.Uniform() * Block), 0.5f * period, air, PlumeLargestMm(hb), PlumeHeard(hb) * WhitecapPart, hiss: true);
                }
                continue;
            }
            p.Crowd += dt;
            if (p.Crowd < CrowdSeconds) continue;
            p.Crowd -= CrowdSeconds;
            int breaks = _rng.Poisson(p.Rate * CrowdSeconds);
            if (breaks <= 0) continue;
            // Their air together, each its share of the piece's (the air a second over the breaks a second).
            float together = p.Air / p.Rate * breaks;
            StartCloud(j, (int)(_rng.Uniform() * Block), CrowdSeconds + 0.5f * t, together, PlumeLargestMm(p.Height), PlumeHeard(p.Height) * WhitecapPart, hiss: true);
        }
    }

    /// <summary>How far apart two bearings are, degrees, −180 to 180.</summary>
    private static float DegreesApart(float a, float b) => (float)Math.IEEERemainder(a - b, 360.0);
}

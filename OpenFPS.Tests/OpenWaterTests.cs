using System.Numerics;
using OpenFPS.Sound;
using OpenFPS.Sound.Core.Nature;
using OpenFPS.Models;
using OpenFPS.Environment;
using OpenFPS.Physics;
using OpenFPS.Materials;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Whitecaps over open water (docs/RUNNING_WATER.md 15.11): a pond's surface heard from the whole of itself, cut at the ear
/// by bearings, each piece the crests breaking in it; none under 3 m/s, more where the fetch is long and where a gust is
/// crossing; nothing in it stepping, on or off (Cody, 2026-10-17: "make sure the sound doesn't step/cut on/cut off").
/// </summary>
public class OpenWaterTests
{
    private readonly ITestOutputHelper _o;
    public OpenWaterTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;

    /// <summary>A round pond, its outline anticlockwise, on the ground at zero.</summary>
    private static Vector3[] Round(float radius, float x = 0f)
        => Enumerable.Range(0, 65).Select(i => new Vector3(x + radius * MathF.Cos(MathF.Tau * i / 64), 0f, radius * MathF.Sin(MathF.Tau * i / 64))).ToArray();

    /// <summary>A pond 200 m east to west and 60 m north to south.</summary>
    private static Vector3[] Long() => new[]
    {
        new Vector3(-100f, 0f, -30f), new Vector3(100f, 0f, -30f), new Vector3(100f, 0f, 30f), new Vector3(-100f, 0f, 30f), new Vector3(-100f, 0f, -30f),
    };

    /// <summary>An open water's key carries its area; its spec is as loud as its area against the hectare its level was
    /// declared over; it is cut as an area, never laid out or taken for an outline; calm under 3 m/s.</summary>
    [Fact]
    public void AnOpenWatersKeyIsItsArea()
    {
        string key = ShoreSpec.OpenKey("shore:open_pond", 18446.4f);
        Assert.Equal("shore:open_pond/open/18446", key);
        Assert.True(ShoreSpec.TryOpen(key, out string preset, out float area));
        Assert.Equal("open_pond", preset);
        Assert.Equal(18446f, area);
        Assert.False(ShoreSpec.TryOutline(key, out _, out _));
        Assert.False(ShoreSpec.TryOpen("shore:pond_bank/outline/410", out _, out _));
        var spec = ModelLibrary.Presets.Shore(key);
        Assert.Equal(ShoreFace.Open, spec.Face);
        Assert.Equal(ShoreSpec.OpenPond.SourceLevelDb + 10f * MathF.Log10(18446f / 10000f), spec.SourceLevelDb, 3);
        var wide = new WideSources();
        Assert.True(wide.IsOpenWater(key));
        Assert.False(wide.IsOutline(key));
        Assert.Null(wide.Layout(key, ModelLibrary.Presets));
        Assert.True(spec.CalmAt(2.9f));
        Assert.False(spec.CalmAt(3.1f));
        Assert.Equal(MathF.PI * 2500f, ShoreSpec.AreaOf(Round(50f)), 0.02f * MathF.PI * 2500f);
    }

    /// <summary>
    /// Cut from its shore and from over it, at every count of pieces, the pieces cover the surface once (their areas its
    /// area) and together are the surface heard from every point of it, by the mixer's law (within half a decibel of the
    /// law over its whole area); each piece's place is on the water.
    /// </summary>
    [Fact]
    public void AnAreasPiecesCoverItOnceAndTogetherAreTheWholeSurface()
    {
        var outline = Round(50f);
        var path = new LinePath(outline);
        foreach (var ear in new[] { new Vector3(52f, 1.7f, 0f), new Vector3(0f, 1.7f, 0f), new Vector3(30f, 1.7f, 20f), new Vector3(-150f, 1.7f, 40f) })
        {
            double whole = ExtendedSources.AreaMeanLawPower(path, ear, 2f);
            int wanted = ExtendedSources.WantedAreaPieces(path, ear, 0);
            foreach (int n in new[] { 1, 3, wanted, ExtendedSources.MaxPlaces })
            {
                var cut = ExtendedSources.CutArea(path, ear, n, 2f);
                var area = cut.Area!;
                Assert.InRange(area.TotalArea, 0.97f * MathF.PI * 2500f, 1.03f * MathF.PI * 2500f);
                double sum = 0;
                for (int k = 0; k < cut.Count; k++)
                {
                    int i = cut.PieceOfPlace(k);
                    var p = ExtendedSources.PlaceArea(cut, ear, i, 2f);
                    Assert.True(new Vector2(p.At.X, p.At.Z).Length() < 50.5f, $"piece {i} placed at {p.At}");
                    float g = p.Gain * MathF.Min(1f, 2f / Vector3.Distance(ear, p.At));
                    sum += cut.ShareOfPlace(k) * (double)g * g;
                }
                double off = 10 * Math.Log10(sum / whole);
                _o.WriteLine($"ear {ear}: {n} pieces, {area.TotalArea:F0} m², the pieces {off:+0.00;-0.00} dB on the whole");
                Assert.True(Math.Abs(off) < 0.5, $"{n} pieces at {ear}: {off:F2} dB off");
            }
        }
    }

    /// <summary>
    /// Walking along the shore in 5 cm steps (a tap is 15 cm), cut again as the client cuts it, the surface heard as a
    /// whole never steps (under 0.3 dB a step) and between cuts no piece's voice steps (under 1 dB a step): each piece is
    /// placed afresh from where the ear is (ExtendedSources.PlaceArea), not held at where it was cut.
    /// </summary>
    [Fact]
    public void WalkingTheShoreNoPieceSteps()
    {
        var outline = Round(50f);
        var path = new LinePath(outline);
        ExtendedSources.LineCut? cut = null;
        Vector3 cutAt = default;
        int wanted = 0;
        double lastTotal = double.NaN;
        float[]? lastGains = null;
        double worstTotal = 0, worstPiece = 0, worstOff = 0;
        int cuts = 0;
        for (float a = -0.6f; a <= 0.6f; a += 0.05f / 52f)
        {
            var ear = new Vector3(52f * MathF.Cos(a), 1.7f, 52f * MathF.Sin(a));
            wanted = ExtendedSources.WantedAreaPieces(path, ear, wanted);
            bool recut = cut == null || cut.Count != wanted || Vector3.Distance(ear, cutAt) > cut.RecutMetres;
            if (recut) { cut = ExtendedSources.CutArea(path, ear, wanted, 2f); cutAt = ear; cuts++; }
            var gains = new float[cut!.Count];
            double total = 0;
            for (int k = 0; k < cut.Count; k++)
            {
                var p = ExtendedSources.PlaceArea(cut, ear, cut.PieceOfPlace(k), 2f);
                gains[k] = p.Gain * MathF.Min(1f, 2f / Vector3.Distance(ear, p.At));
                total += cut.ShareOfPlace(k) * (double)gains[k] * gains[k];
            }
            if (!double.IsNaN(lastTotal)) worstTotal = Math.Max(worstTotal, Math.Abs(10 * Math.Log10(total / lastTotal)));
            worstOff = Math.Max(worstOff, Math.Abs(10 * Math.Log10(total / ExtendedSources.AreaMeanLawPower(path, ear, 2f))));
            if (!recut && lastGains != null)
                for (int k = 0; k < gains.Length; k++)
                    if (gains[k] > 0f && lastGains[k] > 0f) worstPiece = Math.Max(worstPiece, Math.Abs(20 * Math.Log10(gains[k] / lastGains[k])));
            lastTotal = total;
            lastGains = gains;
        }
        _o.WriteLine($"{cuts} cuts; the whole's largest step {worstTotal:F3} dB, a piece's between cuts {worstPiece:F3} dB; at most {worstOff:F3} dB off the surface heard whole");
        Assert.True(worstOff < 0.1, $"the pieces were {worstOff:F2} dB off the surface heard whole");
        Assert.True(cuts > 5, "the walk was never cut again");
        Assert.True(worstTotal < 0.3, $"the whole stepped {worstTotal:F2} dB");
        Assert.True(worstPiece < 1.0, $"a piece stepped {worstPiece:F2} dB");
    }

    /// <summary>
    /// The breaking law has no switch: zero up to 3 m/s, then rising from nothing over the next metre a second (the onset
    /// ramp) and on with the sea's steepness, never jumping between winds a centimetre a second apart; and a gust breaks
    /// more crests than the mean wind, a lull fewer.
    /// </summary>
    [Fact]
    public void TheBreakingLawRisesFromNothingAndFollowsTheGusts()
    {
        float last = 0f, worst = 0f;
        for (float u = 2f; u <= 14f; u += 0.01f)
        {
            var (hs, tp) = WindWaves.FetchLimited(u, 120f, 1.5f);
            float b = WindWaves.GustBreaking(hs, tp, u, u);
            if (u <= WindWaves.WhitecapOnsetWind) Assert.Equal(0f, b);
            worst = MathF.Max(worst, MathF.Abs(b - last));
            last = b;
        }
        _o.WriteLine($"the largest change of the share breaking between winds 1 cm/s apart: {worst:F4}");
        Assert.True(worst < 0.005f, $"the share breaking jumped {worst:F4}");
        // A lake's older sea, under the half of its crests that breaking holds to.
        var (h8, t8) = WindWaves.FetchLimited(8f, 3000f, 5f);
        float gust = WindWaves.GustBreaking(h8, t8, 8f, 10f), mean = WindWaves.GustBreaking(h8, t8, 8f, 8f), lull = WindWaves.GustBreaking(h8, t8, 8f, 6f);
        _o.WriteLine($"8 m/s over 3 km: {mean:P1} breaking, {gust:P1} in a 10 m/s gust, {lull:P1} in a 6 m/s lull");
        Assert.True(gust > 1.1f * mean && lull < 0.9f * mean);
    }

    /// <summary>The surface heard as its pieces summed by the law from the ear, Pa, mono; and the air each piece folds under.</summary>
    private static (float[] Mono, List<float[]> Air) Render(Vector3[] outline, Vector3 ear, Func<float, float> wind, float seconds, float from = 270f, int seed = 3)
    {
        var path = new LinePath(outline);
        var cut = ExtendedSources.CutArea(path, ear, ExtendedSources.WantedAreaPieces(path, ear, 0), 2f);
        var factor = Enumerable.Range(0, cut.Count).Select(k =>
        {
            var p = ExtendedSources.PlaceArea(cut, ear, cut.PieceOfPlace(k), 2f);
            return p.Gain * MathF.Min(1f, 2f / Vector3.Distance(p.At, ear));
        }).ToArray();
        var spec = ShoreSpec.OpenPond.AcrossArea(ShoreSpec.AreaOf(outline));
        var weather = WindWeather.Steady(wind(0f), from, 0.25f);
        var synth = new ShoreSynth(spec, Rate, seed, null, ExtendedSources.MaxPlaces) { Cut = cut, WindFromDegrees = from, Gusts = weather, WindSpeed = wind(0f) };
        var buf = new float[synth.Places];
        var mono = new float[(int)(seconds * Rate)];
        var air = new List<float[]>();
        double now = 100.0;
        for (int i = 0; i < mono.Length; i++)
        {
            if (i % 256 == 0)
            {
                if (i % Rate < 256)
                {
                    weather = weather.Following(WindAir.FromCompass(wind(i / (float)Rate), from, 0.25f), now);
                    synth.Gusts = weather;
                    air.Add(synth.OpenColumns.Select(c => c.Air).ToArray());
                }
                synth.WindSpeed = weather.At(now).Speed;
                synth.Now = now;
                synth.Control(256f / Rate);
                now += 256.0 / Rate;
            }
            synth.NextPlaces(buf);
            float y = 0f;
            for (int k = 0; k < factor.Length; k++) y += buf[k] * factor[k];
            mono[i] = y;
        }
        return (mono, air);
    }

    /// <summary>Its level every 100 ms, dB re 20 µPa.</summary>
    private static List<double> Envelope(float[] x)
    {
        var env = new List<double>();
        for (int s = 0; s + Rate / 10 <= x.Length; s += Rate / 10)
        {
            double e = 0;
            for (int i = s; i < s + Rate / 10; i++) e += x[i] * (double)x[i];
            env.Add(10 * Math.Log10(Math.Max(1e-20, e / (Rate / 10)) / 4e-10));
        }
        return env;
    }

    /// <summary>
    /// How hard it steps: every 2 ms frame's energy against the median of the 50 ms round it (held to a thousandth of the
    /// mean over the second round it, so a sound rising out of silence is judged against itself, as AudioLab --band-steps),
    /// the worst, dB; of the sound and of its second difference, where an edge (a click) stands out.
    /// </summary>
    private static (double Sound, double Edge) Steps(float[] x)
    {
        double Worst(Func<int, double> at)
        {
            const int Frame = Rate / 500, Round = 12, Second = 250;
            var e = new List<double>();
            for (int s = 2; s + Frame <= x.Length; s += Frame)
            {
                double acc = 0;
                for (int i = s; i < s + Frame; i++) { double v = at(i); acc += v * v; }
                e.Add(acc / Frame + 1e-30);
            }
            var prefix = new double[e.Count + 1];
            for (int i = 0; i < e.Count; i++) prefix[i + 1] = prefix[i] + e[i];
            double worst = 0;
            for (int i = Round; i < e.Count - Round; i++)
            {
                var near = e.GetRange(i - Round, 2 * Round + 1).OrderBy(v => v).ToList();
                int lo = Math.Max(0, i - Second), hi = Math.Min(e.Count, i + Second);
                double floor = 1e-3 * (prefix[hi] - prefix[lo]) / (hi - lo);
                if (floor < 1e-25) continue;   // silence
                worst = Math.Max(worst, 10 * Math.Log10(e[i] / Math.Max(near[Round], floor)));
            }
            return worst;
        }
        return (Worst(i => x[i]), Worst(i => x[i] - 2.0 * x[i - 1] + x[i - 2]));
    }

    /// <summary>
    /// The wind falling from 12 m/s to calm and rising back over a minute, the sea already up: the whitecaps fade out and
    /// come back without a step. No 2 ms of it stands out of the 50 ms round it, nor has an edge, more than a steady
    /// 12 m/s's own crests coming and going do (plus 3 dB); silent in the calm, sounding again as it rises.
    /// </summary>
    [Fact]
    public void WhitecapsFadeOutAndBackWithoutAStep()
    {
        var outline = Round(100f);
        var ear = new Vector3(102f, 1.7f, 0f);
        var (steady, _) = Render(outline, ear, _ => 12f, 20f);
        var (ramp, _) = Render(outline, ear, t => 12f * MathF.Abs(1f - t / 30f), 60f);
        var env = Envelope(ramp);
        double quiet = env.Skip(290).Take(20).Max(), loud = 10 * Math.Log10(env.Take(50).Average(v => Math.Pow(10, v / 10)));
        var s = Steps(steady);
        var r = Steps(ramp);
        _o.WriteLine($"{loud:F1} dB at 12 m/s, at most {quiet:F1} dB in the calm; worst 2 ms over the 50 ms round it: steady {s.Sound:F1} dB (edges {s.Edge:F1}), the ramp {r.Sound:F1} dB (edges {r.Edge:F1})");
        Assert.True(loud > 20.0, $"the whitecaps at 12 m/s only {loud:F1} dB");
        Assert.True(quiet < loud - 30.0, $"the calm still {quiet:F1} dB");
        Assert.True(r.Sound < s.Sound + 3.0, $"the ramp stepped {r.Sound:F1} dB (steady {s.Sound:F1})");
        Assert.True(r.Edge < s.Edge + 3.0, $"the ramp has an edge of {r.Edge:F1} dB (steady {s.Edge:F1})");
        // And neither has an edge of its own: every break rises and falls over its envelope.
        Assert.True(Math.Max(s.Edge, r.Edge) < 8.0, $"an edge of {Math.Max(s.Edge, r.Edge):F1} dB");
        Assert.True(Math.Max(s.Sound, r.Sound) < 12.0, $"a step of {Math.Max(s.Sound, r.Sound):F1} dB");
    }

    /// <summary>
    /// The wind blowing from the west over a pond 200 m long: the crests break where the water has run long enough under
    /// it, to leeward. The pieces on the east of it fold far more air under than those on the west.
    /// </summary>
    [Fact]
    public void WhitecapsBreakToLeeward()
    {
        var outline = Long();
        var ear = new Vector3(0f, 1.7f, 0f);
        var path = new LinePath(outline);
        var cut = ExtendedSources.CutArea(path, ear, ExtendedSources.MaxPlaces, 2f);
        var (_, air) = Render(outline, ear, _ => 12f, 8f);
        var last = air[^1];
        double east = 0, west = 0;
        for (int k = 0; k < cut.Count; k++)
        {
            var at = ExtendedSources.PlaceArea(cut, ear, cut.PieceOfPlace(k), 2f).At;
            if (at.X > 10f) east += last[k];
            if (at.X < -10f) west += last[k];
            _o.WriteLine($"piece {k} at ({at.X:F0}, {at.Z:F0}): {last[k] * 1e6:F1} mL/s");
        }
        Assert.True(east > 3.0 * west && east > 0, $"east {east * 1e6:F1} mL/s, west {west * 1e6:F1}");
    }

    /// <summary>A whole outline's shore no longer counts the whitecaps of the water in front of it (the open water's own now);
    /// a stretch of shore, which has no open water beside it, still does.</summary>
    [Fact]
    public void AnOutlinesShoreLeavesTheWhitecapsToTheOpenWater()
    {
        double Energy(ShoreSynth s)
        {
            s.CloudPart = s.PlumePart = s.CrashPart = s.FrontPart = s.SprayPart = s.FoamPart = s.VentPart = s.StonePart = s.PocketPart = s.SlapPart = s.HullPart = 0f;
            var buf = new float[s.Places];
            double e = 0;
            for (int i = 0; i < Rate * 20; i++)
            {
                if (i % 256 == 0) s.Control(256f / Rate);
                s.NextPlaces(buf);
                foreach (float v in buf) e += v * (double)v;
            }
            return e;
        }
        var lake = ModelLibrary.Presets.Shore("lake_sand");
        var stretch = new ShoreSynth(lake, Rate, 5) { WindSpeed = 12f, WindFromDegrees = 0f };
        var path = new LinePath(Round(400f));
        var outline = new ShoreSynth(lake.AlongOutline(path.Length), Rate, 5, null, ExtendedSources.MaxPlaces)
            { WindSpeed = 12f, WindFromDegrees = 270f, Cut = ExtendedSources.Cut(path, new Vector3(402f, 1.7f, 0f), ExtendedSources.MaxPlaces) };
        double s = Energy(stretch), o = Energy(outline);
        _o.WriteLine($"whitecaps alone: a stretch {s:E2} Pa² s, an outline {o:E2}");
        Assert.True(s > 0);
        Assert.Equal(0.0, o);
    }
}

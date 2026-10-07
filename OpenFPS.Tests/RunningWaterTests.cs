using System.Numerics;
using System.Text.Json;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Nature;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Running water (docs/RUNNING_WATER.md): the hydraulics a channel's sound follows from, the run-off
/// that fills a gutter and keeps it running after the rain, and the synth's physics — silent without
/// water, louder with more, drips when there is almost none, places that add up to the source and are
/// never copies of one another.
/// </summary>
[Collection("Runoff")]
public class RunningWaterTests
{
    private readonly ITestOutputHelper _o;
    public RunningWaterTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;

    private static double Db(double meanSquare) => 10 * Math.Log10(Math.Max(1e-30, meanSquare) / 4e-10);

    private static float[] Render(RunningWaterSpec spec, float flow, float seconds, int seed = 3, float spread = 0f)
    {
        var s = new RunningWaterSynth(spec, Rate, seed) { Flow = flow, Spread = spread };
        for (int i = 0; i < Rate; i++) { if (i % 256 == 0) s.Control(256f / Rate); s.Next(); }
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 256 == 0) s.Control(256f / Rate);
            x[i] = s.Next();
        }
        return x;
    }

    /// <summary>The checkout this test file is in (as NatureTests finds it).</summary>
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    private static double MeanSquare(float[] x) => x.Sum(v => (double)v * v) / x.Length;

    // ── Hydraulics ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_stream_bed_is_as_deep_as_Mannings_law_says()
    {
        var spec = RunningWaterSpec.Creek;
        foreach (float q in new[] { 5f, 40f, 300f })
        {
            var st = Hydraulics.Of(spec, q);
            float w = spec.WidthMetres, y = st.DepthMetres;
            float r = w * y / (w + 2f * y);
            float back = w * y * MathF.Pow(r, 2f / 3f) * MathF.Sqrt(spec.Slope) / spec.ManningN * 1000f;
            _o.WriteLine($"  {q} L/s: {y * 100:F2} cm at {st.SpeedMetresPerSecond:F2} m/s, Fr {st.Froude:F2}");
            Assert.InRange(back / q, 0.999, 1.001);
            Assert.InRange(st.SpeedMetresPerSecond * w * y * 1000f / q, 0.999, 1.001);
        }
        // More water is deeper and faster.
        Assert.True(Hydraulics.Of(spec, 80f).DepthMetres > Hydraulics.Of(spec, 40f).DepthMetres);
        Assert.True(Hydraulics.Of(spec, 80f).SpeedMetresPerSecond > Hydraulics.Of(spec, 40f).SpeedMetresPerSecond);
    }

    [Fact]
    public void A_kerb_gutter_spreads_as_road_drainage_is_designed()
    {
        // FHWA HEC-22 (4th ed.), eq. 4-2 in SI: Q = (0.376/n) Sx^(5/3) SL^(1/2) T^(8/3). With n 0.016, a
        // 2.5 % cross-fall and a 1 % fall along it, a 2.5 m spread carries 0.0576 m³/s.
        var spec = RunningWaterSpec.KerbGutter with { ManningN = 0.016f, CrossSlope = 0.025f, Slope = 0.01f };
        var st = Hydraulics.Of(spec, 57.6f);
        _o.WriteLine($"  57.6 L/s: spread {st.WettedWidthMetres:F3} m, {st.DepthMetres * 1000:F1} mm at the kerb, {st.SpeedMetresPerSecond:F2} m/s");
        Assert.InRange(st.WettedWidthMetres, 2.48f, 2.52f);
        Assert.InRange(st.DepthMetres, 0.0620f, 0.0630f);
    }

    [Fact]
    public void A_weir_passes_its_flow_over_its_head()
    {
        float h = Hydraulics.WeirHead(1.11f, 0.1f);
        Assert.InRange(1.83f * 0.1f * MathF.Pow(h, 1.5f) * 1000f, 1.10f, 1.12f);
    }

    // ── Run-off ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_catchment_fills_and_runs_on_as_a_linear_reservoir()
    {
        try
        {
            Runoff.Reset();
            Runoff.Update(0f, 0);
            // Rain starts: after one time constant 63 % of the way, after five nearly all.
            for (int t = 1; t <= 120; t++) Runoff.Update(10f, t);
            Assert.InRange(Runoff.Through(120f), 6.2f, 6.45f);
            for (int t = 121; t <= 600; t++) Runoff.Update(10f, t);
            Assert.InRange(Runoff.Through(120f), 9.9f, 10f);
            // It stops: one time constant later a third is left, and a slower catchment keeps more.
            for (int t = 601; t <= 720; t++) Runoff.Update(0f, t);
            float fast = Runoff.Through(120f), slow = Runoff.Through(960f);
            _o.WriteLine($"  2 min after: τ 120 s {fast:F2} mm/h, τ 960 s {slow:F2} mm/h");
            Assert.InRange(fast, 3.6f, 3.75f);
            Assert.True(slow > fast);
            // A catchment between two rungs is read between them.
            float between = Runoff.Through(170f), lo = MathF.Min(Runoff.Through(120f), Runoff.Through(240f)), hi = MathF.Max(Runoff.Through(120f), Runoff.Through(240f));
            Assert.InRange(between, lo, hi);
        }
        finally { Runoff.Reset(); }
    }

    [Fact]
    public void Someone_arriving_in_rain_finds_the_gutters_running()
    {
        try
        {
            Runoff.Reset();
            Runoff.Update(Rainfall.HeavyRate, 1000.0);
            var gutter = RunningWaterSpec.KerbGutter;
            Assert.InRange(Runoff.Through(gutter.CatchmentSeconds), Rainfall.HeavyRate * 0.999f, Rainfall.HeavyRate * 1.001f);
            Assert.True(gutter.FlowFor(Runoff.Through(gutter.CatchmentSeconds)) > 1f);
        }
        finally { Runoff.Reset(); }
    }

    [Fact]
    public void A_rain_fed_drain_is_dry_without_rain_and_a_creek_never_is()
    {
        var drain = RunningWaterSpec.DrainGrate;
        Assert.True(drain.FlowFor(0f) < RunningWaterSpec.DryLitresPerSecond);
        Assert.True(drain.FlowFor(Rainfall.LightRate) > RunningWaterSpec.DryLitresPerSecond);
        Assert.True(RunningWaterSpec.Creek.FlowFor(0f) > 1f);
    }

    // ── The synth ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("creek")]
    [InlineData("gutter")]
    [InlineData("drain_grate")]
    [InlineData("downpipe")]
    [InlineData("basin_overflow")]
    public void No_water_no_sound(string preset)
    {
        var x = Render(RunningWaterSpec.ByName(preset), 0f, 2f);
        Assert.Equal(0.0, MeanSquare(x));
    }

    [Theory]
    [InlineData("creek")]
    [InlineData("gutter")]
    [InlineData("drain_grate")]
    [InlineData("downpipe")]
    [InlineData("basin_overflow")]
    public void It_renders_at_its_declared_level_and_is_finite(string preset)
    {
        var spec = RunningWaterSpec.ByName(preset);
        var x = Render(spec, spec.ReferenceFlow, 20f, seed: 7);
        Assert.All(x, v => Assert.True(float.IsFinite(v)));
        double db = Db(MeanSquare(x));
        _o.WriteLine($"  {preset}: {db:F1} dB at 1 m, declared {spec.SourceLevelDb:F1}");
        Assert.InRange(db, spec.SourceLevelDb - 2.0, spec.SourceLevelDb + 2.0);
    }

    [Theory]
    [InlineData("creek", 10f, 40f, 160f)]
    [InlineData("gutter", 0.08f, 0.27f, 1.34f)]
    [InlineData("drain_grate", 0.1f, 0.36f, 1.8f)]
    public void More_water_is_louder(string preset, float low, float mid, float high)
    {
        var spec = RunningWaterSpec.ByName(preset);
        double a = Db(MeanSquare(Render(spec, low, 15f))), b = Db(MeanSquare(Render(spec, mid, 15f))), c = Db(MeanSquare(Render(spec, high, 15f)));
        _o.WriteLine($"  {preset}: {low} L/s {a:F1} dB, {mid} L/s {b:F1} dB, {high} L/s {c:F1} dB");
        Assert.True(b > a + 1.0 && c > b + 1.0);
    }

    [Fact]
    public void A_downpipe_with_almost_nothing_in_it_drips_at_the_rate_its_lip_lets_go()
    {
        var spec = RunningWaterSpec.Downpipe;
        float flow = 0.001f;                    // a millilitre a second
        var x = Render(spec, flow, 20f);
        // Count the drops: 5 ms peaks standing well over the quiet between them.
        int w = Rate / 200, count = 0;
        double floor = 1e-12;
        var peaks = new List<double>();
        for (int i = 0; i + w <= x.Length; i += w) peaks.Add(x.Skip(i).Take(w).Max(v => Math.Abs((double)v)));
        double median = peaks.OrderBy(p => p).ElementAt(peaks.Count / 2) + floor;
        for (int i = 1; i < peaks.Count; i++) if (peaks[i] > 30 * median && peaks[i - 1] <= 30 * median) count++;
        float volume = 2f * MathF.PI * spec.DripLipMm * 1e-3f * 0.072f * 0.6f / (1000f * 9.81f);
        float expected = flow * 1e-3f / volume * 20f;
        _o.WriteLine($"  {count} drops in 20 s, Tate's law says {expected:F0}");
        Assert.InRange(count, expected * 0.8f, expected * 1.2f);
    }

    [Fact]
    public void Its_places_add_up_to_the_source_and_are_not_copies()
    {
        var spec = RunningWaterSpec.Creek;
        var whole = new RunningWaterSynth(spec, Rate, 5) { Flow = spec.ReferenceFlow, Spread = 1f };
        var split = new RunningWaterSynth(spec, Rate, 5) { Flow = spec.ReferenceFlow, Spread = 1f };
        int n = split.Places;
        Span<float> places = stackalloc float[n];
        var power = new double[n];
        var a = new float[Rate * 10];
        var b = new float[n][];
        for (int k = 0; k < n; k++) b[k] = new float[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            if (i % 256 == 0) { whole.Control(256f / Rate); split.Control(256f / Rate); }
            float y = whole.Next();
            split.NextPlaces(places);
            float sum = 0f;
            for (int k = 0; k < n; k++) { sum += places[k]; power[k] += places[k] * (double)places[k]; b[k][i] = places[k]; }
            Assert.Equal(y, sum, 4);
        }
        double total = power.Sum();
        _o.WriteLine("  shares: " + string.Join(" ", power.Select(p => (p / total).ToString("F2"))));
        // Fully spread, each place carries about its share (one in n); never all at one.
        Assert.All(power, p => Assert.InRange(p / total, 0.4 / n, 2.2 / n));
        // Independent streams: neighbouring places do not correlate.
        for (int k = 1; k < n; k++)
        {
            double xy = 0, xx = 0, yy = 0;
            for (int i = 0; i < a.Length; i++) { xy += b[0][i] * (double)b[k][i]; xx += b[0][i] * (double)b[0][i]; yy += b[k][i] * (double)b[k][i]; }
            Assert.InRange(xy / Math.Sqrt(xx * yy), -0.1, 0.1);
        }
    }

    [Fact]
    public void Merged_a_source_is_all_at_its_middle()
    {
        var spec = RunningWaterSpec.KerbGutter;
        var s = new RunningWaterSynth(spec, Rate, 9) { Flow = 1f, Spread = 0f };
        Span<float> places = stackalloc float[s.Places];
        double outer = 0, middle = 0;
        for (int i = 0; i < Rate * 5; i++)
        {
            if (i % 256 == 0) s.Control(256f / Rate);
            s.NextPlaces(places);
            middle += places[0] * (double)places[0];
            for (int k = 1; k < places.Length; k++) outer += places[k] * (double)places[k];
        }
        Assert.True(middle > 0);
        Assert.Equal(0.0, outer);
    }

    [Fact]
    public void A_line_source_is_laid_along_its_length_and_a_ring_round_its_middle()
    {
        var creek = RunningWaterSynth.Layout(RunningWaterSpec.Creek);
        Assert.Equal(RunningWaterSpec.Creek.Places, creek.Length);
        Assert.Equal(0f, creek[0].Length());
        var xs = creek.Select(p => p.X).OrderBy(v => v).ToArray();
        _o.WriteLine("  creek places at x = " + string.Join(", ", xs.Select(v => v.ToString("F1"))));
        Assert.All(creek, p => { Assert.Equal(0f, p.Y); Assert.Equal(0f, p.Z); Assert.InRange(MathF.Abs(p.X), 0f, RunningWaterSpec.Creek.LengthMetres / 2f); });
        Assert.Equal(creek.Length, xs.Distinct().Count());
        var grate = RunningWaterSynth.Layout(RunningWaterSpec.DrainGrate);
        Assert.All(grate.Skip(1), p => Assert.InRange(p.Length(), 0.24f, 0.26f));
        // And the client finds the same.
        Assert.Equal(creek.Length, ExtendedSources.Layout("flow:creek")?.Length);
        Assert.Null(ExtendedSources.Layout("flow:no_such_thing"));
    }

    // ── Texture: fitted against recordings ─────────────────────────────────────────────────────

    /// <summary>The features the fountain, rain and leaves were fitted on (NatureTests.Fitted).</summary>
    private static readonly string[] Fitted =
    {
        "cv 1-3k", "cv 3-6k", "cv 6-12k", "skew 1-3k", "skew 3-6k", "skew 6-12k", "kurt 3-6k", "kurt 6-12k",
        "corr near", "corr octave", "corr far", "mod slow", "mod mid", "mod fast",
    };

    /// <summary>As NatureTests.HoldInRange: inside the recordings' spread, widened by a fifth of it and
    /// by what twenty seconds of one seed wanders.</summary>
    private void HoldInRange(string texture, Dictionary<string, double> model, IEnumerable<string> keys, int allowed = 0)
    {
        var misses = new List<string>();
        foreach (var k in keys)
        {
            var (lo, hi) = TextureStatistics.Range(texture, k);
            double floor = k.StartsWith("skew") ? 0.05 : k.StartsWith("kurt") ? 0.25 : k.StartsWith("mod") ? 0.02 : 0.01;
            double margin = Math.Max(0.2 * (hi - lo), floor);
            bool ok = model[k] >= lo - margin && model[k] <= hi + margin;
            _o.WriteLine($"  {k,-12} {model[k],7:F3}   recordings {lo,7:F3} .. {hi,7:F3}{(ok ? "" : "   OUTSIDE")}");
            if (!ok) misses.Add($"{k} {model[k]:F3} not in {lo:F3}..{hi:F3}");
        }
        Assert.True(misses.Count <= allowed, string.Join("; ", misses));
    }

    /// <summary>
    /// A creek is a dense babble, not a few gurgles over silence (2026-10-06): held on the cochlear
    /// statistics against six recorded brooks and creeks and two river riffles, and inside 10 ms a wash
    /// with a few plinks, as they are.
    /// </summary>
    [Fact]
    public void A_creek_moves_as_recorded_creeks_do()
    {
        var x = Render(RunningWaterSpec.Creek, RunningWaterSpec.Creek.ReferenceFlow, 20f, seed: 3);
        HoldInRange("stream", TextureStatistics.Analyse(x).Summary(), Fitted);
        var (kurtosis, crest) = TextureStatistics.Waveform(x);
        _o.WriteLine($"  4-16 kHz in 10 ms: kurtosis {kurtosis:F2}, crest {crest:F1} dB; recordings {TextureStatistics.StreamWaveformKurtosisMin:F2}-{TextureStatistics.StreamWaveformKurtosisMax:F2}");
        Assert.InRange(kurtosis, TextureStatistics.StreamWaveformKurtosisMin - 0.2, 4.5);
    }

    /// <summary>
    /// A drain and a downpipe in moderate rain against four recorded drains and five downpipes: all but
    /// one statistic inside. The one that is not is the top band's spikiness: the drain's 6-12 kHz
    /// envelope a little peakier than any recorded drain's (7.1 against 6.3), the downpipe's a little
    /// smoother than any recorded downpipe's (skew 0.8 against 1.1-2.1, recorded closer, in heavier flows).
    /// </summary>
    [Theory]
    [InlineData("drain_grate", "drain")]
    [InlineData("downpipe", "downpipe")]
    public void A_drain_and_a_downpipe_move_as_recorded_ones_do(string preset, string texture)
    {
        var spec = RunningWaterSpec.ByName(preset);
        var x = Render(spec, spec.ReferenceFlow, 20f, seed: 3);
        HoldInRange(texture, TextureStatistics.Analyse(x).Summary(), Fitted, allowed: 1);
        var (kurtosis, _) = TextureStatistics.Waveform(x);
        _o.WriteLine($"  4-16 kHz in 10 ms: kurtosis {kurtosis:F2}");
        // No needle-sharp clicks: drops on wet stone build through the film and splash (HardCushion).
        Assert.InRange(kurtosis, 2.8, 7.0);
    }

    /// <summary>
    /// The city's gutter and its drain, on Foundry Street's south kerb by the spawn: each a model this
    /// client knows, the gutter lying along the kerb, neither inside anything solid (a source inside a
    /// box is heard through it), both rain-fed so they are silent in dry weather.
    /// </summary>
    [Fact]
    public void The_gutter_and_its_drain_are_on_the_city_by_the_spawn()
    {
        string root = RepoRoot();
        var prefabs = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(Path.Combine(root, "OpenFPS.Server", "prefabs"), "*.json"))
        {
            if (path.EndsWith("prefab-schema.json")) continue;
            var doc = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
            prefabs[doc.GetProperty("Id").GetString()!] = doc;
        }
        var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "OpenFPS.Server", "maps", "city.json"))).RootElement;
        var solids = new List<(Vector3 Centre, Vector3 Half, string Name)>();
        var water = new List<(string Prefab, Vector3 At, bool Turned)>();
        foreach (var e in map.GetProperty("Entities").EnumerateArray())
        {
            string id = e.GetProperty("PrefabId").GetString()!;
            if (!prefabs.TryGetValue(id, out var p)) continue;
            var pos = e.GetProperty("Position");
            var at = new Vector3(pos.GetProperty("X").GetSingle(), pos.GetProperty("Y").GetSingle(), pos.GetProperty("Z").GetSingle());
            if (id is "gutter_water" or "drain_grate_water") { water.Add((id, at, e.TryGetProperty("Rotation", out _))); continue; }
            bool solid = !p.TryGetProperty("IsSolid", out var so) || so.GetBoolean();
            if (!solid || !p.TryGetProperty("ColliderSize", out var cs)) continue;
            var c = new Vector3(cs.GetProperty("X").GetSingle(), cs.GetProperty("Y").GetSingle(), cs.GetProperty("Z").GetSingle());
            var s = e.TryGetProperty("Scale", out var sc) ? new Vector3(sc.GetProperty("X").GetSingle(), sc.GetProperty("Y").GetSingle(), sc.GetProperty("Z").GetSingle()) : Vector3.One;
            solids.Add((at, c * s / 2f, e.TryGetProperty("Name", out var nm) ? nm.GetString() ?? id : id));
        }
        Assert.Single(water, w => w.Prefab == "gutter_water");
        Assert.Single(water, w => w.Prefab == "drain_grate_water");
        foreach (var (prefab, at, turned) in water)
        {
            string sound = prefabs[prefab].GetProperty("SoundId").GetString()!;
            var spec = RunningWaterSpec.ByName(sound[5..]);
            Assert.True(spec.CatchmentSquareMetres > 0f && spec.BaseFlowLitresPerSecond == 0f, $"{prefab} should be fed by the rain alone");
            var inside = solids.Where(b => MathF.Abs(at.X - b.Centre.X) < b.Half.X && MathF.Abs(at.Y - b.Centre.Y) < b.Half.Y && MathF.Abs(at.Z - b.Centre.Z) < b.Half.Z)
                               .Select(b => b.Name).ToList();
            Assert.True(inside.Count == 0, $"{prefab} at {at} is inside {string.Join(", ", inside)}");
            // Within a few metres of the spawn (60, 122) and against the Foundry Street kerb (z 124).
            Assert.InRange(at.Z, 124f, 124.6f);
            Assert.InRange(at.X, 50f, 70f);
            // The gutter's places lie along its x axis: unturned, along the street.
            if (prefab == "gutter_water") Assert.False(turned);
        }
    }

    [Fact]
    public void The_fountain_is_unchanged_by_the_hooks_running_water_uses()
    {
        // FlowScale 1 and no placer: the fountain renders sample for sample as it did.
        var spec = WaterFeatureSpec.ByName("park_fountain");
        var a = new FallingWaterSynth(spec, Rate, 4);
        var b = new FallingWaterSynth(spec, Rate, 4) { FlowScale = 1f };
        for (int i = 0; i < Rate * 2; i++)
        {
            if (i % 256 == 0) { a.Control(256f / Rate); b.Control(256f / Rate); }
            Assert.Equal(a.Next(), b.Next());
        }
    }
}

[CollectionDefinition("Runoff", DisableParallelization = true)]
public class RunoffCollection { }

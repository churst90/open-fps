using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Nature;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Running water round 2 (docs/RUNNING_WATER.md section 10): water leaving through a hole and gurgling,
/// taps over basins that fill and drain, drips onto steel, falls that grow faster than their flow, the
/// slow store that keeps a downpipe dripping, and where the sinks and downpipes stand on the city.
/// </summary>
[Collection("Runoff")]
public class RunningWaterRound2Tests
{
    private readonly ITestOutputHelper _o;
    public RunningWaterRound2Tests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;

    private static double Db(double meanSquare) => 10 * Math.Log10(Math.Max(1e-30, meanSquare) / 4e-10);

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    /// <summary>Runs a synth with this flow for this long, returning its output.</summary>
    private static float[] Run(RunningWaterSynth s, float flow, float seconds)
    {
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 256 == 0) { s.Flow = flow; s.Control(256f / Rate); }
            x[i] = s.Next();
        }
        return x;
    }

    private static double MeanSquare(float[] x) => x.Sum(v => (double)v * v) / Math.Max(1, x.Length);

    // ── The inlet ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_outlet_passes_what_HR_Wallingfords_gutter_outlets_did()
    {
        // BS 6367's gutter-outlet forms as SR463's table gives them (L/s, mm): weir D h^1.5 / 7000 up to
        // about half the diameter, orifice D² h^0.5 / 13200 above.
        var outlet = new FlowInlet { DiameterMetres = 0.150f };
        foreach (var (hMm, expect) in new[] { (30f, 150f * MathF.Pow(30f, 1.5f) / 7000f), (50.5f, 150f * MathF.Pow(50.5f, 1.5f) / 7000f),
                                               (92.5f, 150f * 150f * MathF.Sqrt(92.5f) / 13200f) })
        {
            float q = RunningWaterSynth.InletCapacity(outlet, hMm * 1e-3f);
            _o.WriteLine($"  {hMm} mm: {q:F2} L/s, BS 6367 {expect:F2}");
            Assert.InRange(q / expect, 0.97f, 1.03f);
            Assert.InRange(RunningWaterSynth.InletDepthFor(outlet, q) * 1e3f, hMm - 0.2f, hMm + 0.2f);
        }
    }

    [Fact]
    public void An_outlet_gurgles_only_between_closing_over_and_its_vortex_drowning()
    {
        var outlet = RunningWaterSpec.GutterOutlet.Inlet!;
        float d = outlet.DiameterMetres;
        // Spilling in round an open air core: nothing.
        Assert.Equal(0f, RunningWaterSynth.GurgleShare(outlet, 0.05f * d, 0.1f));
        // Closed over it, under the vortex's critical submergence: gurgling.
        float flow = RunningWaterSynth.InletCapacity(outlet, 0.5f * d);
        Assert.True(RunningWaterSynth.GurgleShare(outlet, 0.5f * d, flow) > 0.8f);
        // Drowned deep: the vortex no longer reaches it.
        float deep = 8f * d;
        Assert.Equal(0f, RunningWaterSynth.GurgleShare(outlet, deep, RunningWaterSynth.InletCapacity(outlet, deep)));
        // A house roof's outlet in moderate rain spills; in a downpour it gurgles.
        float moderate = RunningWaterSpec.GutterOutlet.FlowFor(Rainfall.ModerateRate), violent = RunningWaterSpec.GutterOutlet.FlowFor(Rainfall.ViolentRate);
        float gm = RunningWaterSynth.GurgleShare(outlet, RunningWaterSynth.InletDepthFor(outlet, moderate), moderate);
        float gv = RunningWaterSynth.GurgleShare(outlet, RunningWaterSynth.InletDepthFor(outlet, violent), violent);
        _o.WriteLine($"  gutter outlet: moderate rain {gm:F2}, violent {gv:F2}");
        Assert.Equal(0f, gm);
        Assert.True(gv > 0.2f);
    }

    // ── The basin ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_sink_fills_while_its_tap_runs_and_drains_with_a_gurgle_when_it_is_shut()
    {
        var spec = RunningWaterSpec.KitchenSink;
        var s = new RunningWaterSynth(spec, Rate, 3) { Flow = 0f };
        var on = Run(s, spec.Tap!.OpenLitresPerSecond, 60f);
        float full = s.Level;
        _o.WriteLine($"  after a minute running: {full * 1000:F1} mm");
        // The plug is in while it runs: 0.12 L/s over a 0.45 by 0.38 m bowl is 0.7 mm a second.
        Assert.InRange(full * 1000f, 36f, 46f);
        Assert.Equal(0f, s.InletFlow);
        // Shut: the plug comes out, it drains, and the gurgle comes only once it is shallow.
        var gurgle = new List<(float T, float Level, float G)>();
        for (int k = 0; k < 120; k++)
        {
            Run(s, 0f, 0.5f);
            gurgle.Add((k * 0.5f, s.Level, RunningWaterSynth.GurgleShare(spec.Inlet!, s.InletDepth, s.InletFlow)));
        }
        Assert.True(s.Level < 0.0005f, $"still {s.Level * 1000:F1} mm after a minute");
        float firstGurgle = gurgle.First(g => g.G > 0.05f).T;
        float emptyAt = gurgle.First(g => g.Level < 0.0005f).T;
        _o.WriteLine($"  first gurgle {firstGurgle:F1} s after shutting, empty at {emptyAt:F1} s");
        Assert.True(gurgle[0].G < 0.05f, "it gurgled while still deep");
        Assert.True(firstGurgle < emptyAt);
        // And after that, nothing: an empty sink with its tap shut is silent.
        var after = Run(s, 0f, 3f);
        Assert.Equal(0.0, MeanSquare(after.Skip(Rate).ToArray()), 12);
    }

    [Fact]
    public void A_tap_on_a_steel_bottom_rings_it_and_water_on_it_damps_the_ringing()
    {
        var spec = RunningWaterSpec.KitchenSink with { Basin = RunningWaterSpec.KitchenSink.Basin! with { PlugWhileRunning = false } };
        double Rendered(float platePart)
        {
            var s = new RunningWaterSynth(spec, Rate, 5) { Flow = 0f, PlatePart = platePart };
            Run(s, 0.12f, 1f);
            return MeanSquare(Run(s, 0.12f, 6f));
        }
        double with = Rendered(1f), without = Rendered(0f);
        _o.WriteLine($"  plug out, tap running: with the plate ringing {Db(with):F1} dB, without {Db(without):F1} dB");
        Assert.True(with > without * 1.05, "the steel does not ring");
        // A ceramic basin has no plate.
        var basin = new RunningWaterSynth(RunningWaterSpec.Washbasin, Rate, 5) { PlatePart = 1f };
        var b0 = Run(basin, 0.08f, 3f);
        basin = new RunningWaterSynth(RunningWaterSpec.Washbasin, Rate, 5) { PlatePart = 0f };
        Assert.Equal(MeanSquare(b0), MeanSquare(Run(basin, 0.08f, 3f)), 9);
    }

    [Fact]
    public void A_dripping_tap_drips_at_the_rate_its_lip_lets_go()
    {
        var spec = RunningWaterSpec.DrippingKitchenSink;
        var s = new RunningWaterSynth(spec, Rate, 9) { Flow = 0f };
        float leak = spec.Tap!.LeakLitresPerSecond;
        Run(s, leak, 2f);
        var x = Run(s, leak, 30f);
        int w = Rate / 200, count = 0;
        var peaks = new List<double>();
        for (int i = 0; i + w <= x.Length; i += w) peaks.Add(x.Skip(i).Take(w).Max(v => Math.Abs((double)v)));
        double median = peaks.OrderBy(p => p).ElementAt(peaks.Count / 2) + 1e-12;
        // An onset is a window far over the quiet and a tenth of the loudest; the steel rings on after each
        // drip, so within 0.6 s of one it is the same drip.
        int last = -1000;
        double loudest = peaks.Max();
        for (int i = 1; i < peaks.Count; i++)
            if (peaks[i] > 30 * median && peaks[i] > 0.1 * loudest && i - last > 120) { count++; last = i; }
        float volume = 2f * MathF.PI * spec.DripLipMm * 1e-3f * 0.072f * 0.6f / (1000f * 9.81f);
        float expected = leak * 1e-3f / volume * 30f;
        _o.WriteLine($"  {count} drips in 30 s, Tate's law {expected:F0}; the sink holds {s.Level * 1000:F2} mm");
        Assert.InRange(count, expected * 0.75f, expected * 1.25f);
    }

    // ── Falls, the downpipe and the slow store ─────────────────────────────────────────────────

    [Fact]
    public void A_fall_grows_faster_than_its_flow_as_its_sheet_thickens()
    {
        var spec = RunningWaterSpec.BasinOverflow;
        double Level(float flow)
        {
            var s = new RunningWaterSynth(spec, Rate, 4) { Flow = flow };
            Run(s, flow, 2f);
            return Db(MeanSquare(Run(s, flow, 12f)));
        }
        double half = Level(0.5f), one = Level(1f);
        _o.WriteLine($"  overflow 0.5 L/s {half:F1} dB, 1 L/s {one:F1} dB: {one - half:F1} dB a doubling (incoherent sum 3; Watts et al. 6 dB(A))");
        Assert.True(one - half > 3.8);
    }

    [Fact]
    public void Water_leaving_a_downpipe_is_already_moving()
    {
        var spec = RunningWaterSpec.Downpipe;
        var shoe = spec.Falls.Single(f => f.FromPipe);
        var shape = RunningWaterSynth.Shape(spec, shoe, 0.3f);
        float terminal = RunningWaterSynth.TerminalFilmSpeed(0.3f, spec.Cavity!.DiameterMetres);
        _o.WriteLine($"  at 0.3 L/s: film {terminal:F2} m/s, lands as if from {shape.FallMetres:F2} m");
        Assert.InRange(shape.FallMetres, shoe.DropMetres + 0.9f * terminal * terminal / (2f * 9.81f), shoe.DropMetres + 1.1f * terminal * terminal / (2f * 9.81f));
    }

    /// <summary>
    /// "the down pipe drains still flange" (Cody, 2026-10-07). The gutter outlet's gulps and the film
    /// striking the shoe are heard through the downpipe's 5.5 m of air, whose round trip is 32.3 ms. Its
    /// open ends let the high notes out (an unflanged pipe's reflection falls as e^(−(ka)²/2)); the old
    /// loop kept a fifth to a third of them up to 6 kHz, so every splash came back every 32 ms: the
    /// cepstrum of 8192-sample frames peaked there in more than half of them. The pipe's low modes stay.
    /// </summary>
    [Fact]
    public void A_downpipe_does_not_hand_its_splashes_back_every_round_trip()
    {
        foreach (var spec in new[] { RunningWaterSpec.GutterOutlet, RunningWaterSpec.Downpipe })
        {
            var s = new RunningWaterSynth(spec, Rate, 7);
            var x = Run(s, 0.3f, 12f);
            var cav = spec.Cavity!;
            float roundTrip = 2f * (cav.LengthMetres + 0.6f * cav.DiameterMetres) / 343f;
            int lag = (int)MathF.Round(roundTrip * Rate);
            const int n = 8192;
            int frames = 0, combs = 0;
            var re = new double[n];
            var im = new double[n];
            for (int at = 2 * Rate; at + n <= x.Length; at += n / 2)
            {
                for (int i = 0; i < n; i++) { re[i] = x[at + i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1))); im[i] = 0; }
                Fft(re, im, false);
                for (int i = 0; i < n; i++) { re[i] = Math.Log(re[i] * re[i] + im[i] * im[i] + 1e-30); im[i] = 0; }
                Fft(re, im, true);
                // z of the cepstrum's peak near the round trip over 0.3-40 ms.
                int a = (int)(0.0003 * Rate), b = (int)(0.040 * Rate);
                var seg = re.Skip(a).Take(b - a).ToArray();
                double med = seg.OrderBy(v => v).ElementAt(seg.Length / 2), sd = Math.Sqrt(seg.Select(v => (v - med) * (v - med)).Average());
                double peak = Enumerable.Range(lag - 2, 5).Max(i => re[i]);
                frames++;
                if ((peak - med) / sd > 6) combs++;
            }
            _o.WriteLine($"  {spec.Name}: round trip {roundTrip * 1e3:F1} ms, comb in {combs} of {frames} frames");
            Assert.True(combs <= frames / 10, $"{spec.Name}: a comb at the pipe's round trip in {combs} of {frames} frames");
        }
    }

    /// <summary>In-place radix-2 FFT; the inverse scales by 1/n.</summary>
    private static void Fft(double[] re, double[] im, bool inverse)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int u = i + k, v = i + k + len / 2;
                    double tr = re[v] * cr - im[v] * ci, ti = re[v] * ci + im[v] * cr;
                    re[v] = re[u] - tr; im[v] = im[u] - ti;
                    re[u] += tr; im[u] += ti;
                    double nr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = nr;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }

    [Fact]
    public void A_roof_drips_long_after_the_rain_through_its_slow_store()
    {
        try
        {
            var spec = RunningWaterSpec.Downpipe;
            var single = spec with { SlowShare = 0f };
            Runoff.Reset();
            Runoff.Update(Rainfall.HeavyRate, 0);
            for (int t = 1; t <= 1200; t++) Runoff.Update(0f, t);
            float two = spec.FlowNow(), one = single.FlowNow();
            _o.WriteLine($"  20 min after heavy rain: one store {one * 1000:F4} mL/s, two {two * 1000:F3} mL/s");
            Assert.True(one < RunningWaterSpec.DryLitresPerSecond);
            Assert.True(two > 10 * RunningWaterSpec.DryLitresPerSecond);
        }
        finally { Runoff.Reset(); }
    }

    // ── On the city ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_flat_has_its_taps_and_every_house_its_downpipe()
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
        var solids = new List<(Vector3 C, Vector3 H, string N)>();
        var fixtures = new List<(string Prefab, Vector3 At)>();
        int sofas = 0, houses = 0;
        string[] ours = { "kitchen_sink_water", "dripping_sink_water", "washbasin_water", "shower_water", "downpipe_water", "gutter_outlet_water" };
        foreach (var e in map.GetProperty("Entities").EnumerateArray())
        {
            string id = e.GetProperty("PrefabId").GetString()!;
            string name = e.TryGetProperty("Name", out var nm) ? nm.GetString() ?? "" : "";
            if (id == "furniture_soft" && name.Contains(" flat ") && name.EndsWith(" sofa")) sofas++;
            if (id == "concrete_floor" && name.EndsWith(" roof") && name.Contains(" Street")) houses++;
            if (!prefabs.TryGetValue(id, out var p)) continue;
            var pos = e.GetProperty("Position");
            var at = new Vector3(pos.GetProperty("X").GetSingle(), pos.GetProperty("Y").GetSingle(), pos.GetProperty("Z").GetSingle());
            if (ours.Contains(id)) { fixtures.Add((id, at)); continue; }
            bool solid = !p.TryGetProperty("IsSolid", out var so) || so.GetBoolean();
            if (!solid || !p.TryGetProperty("ColliderSize", out var cs)) continue;
            var c = new Vector3(cs.GetProperty("X").GetSingle(), cs.GetProperty("Y").GetSingle(), cs.GetProperty("Z").GetSingle());
            var sc = e.TryGetProperty("Scale", out var scl) ? new Vector3(scl.GetProperty("X").GetSingle(), scl.GetProperty("Y").GetSingle(), scl.GetProperty("Z").GetSingle()) : Vector3.One;
            solids.Add((at, c * sc / 2f, name.Length > 0 ? name : id));
        }
        int Count(string p) => fixtures.Count(f => f.Prefab == p);
        _o.WriteLine($"  {sofas} flats, {houses} houses; {Count("kitchen_sink_water")} + {Count("dripping_sink_water")} kitchen sinks, {Count("washbasin_water")} washbasins, {Count("shower_water")} showers, {Count("downpipe_water")} downpipes, {Count("gutter_outlet_water")} outlets");
        Assert.Equal(sofas, Count("kitchen_sink_water") + Count("dripping_sink_water"));
        Assert.Equal(1, Count("dripping_sink_water"));
        Assert.Equal(sofas, Count("washbasin_water"));
        Assert.Equal(sofas, Count("shower_water"));
        Assert.Equal(houses, Count("downpipe_water"));
        Assert.Equal(houses, Count("gutter_outlet_water"));
        // None inside anything solid: a source inside a box is heard through it.
        foreach (var (prefab, at) in fixtures)
        {
            var inside = solids.Where(b => MathF.Abs(at.X - b.C.X) < b.H.X && MathF.Abs(at.Y - b.C.Y) < b.H.Y && MathF.Abs(at.Z - b.C.Z) < b.H.Z).Select(b => b.N).ToList();
            Assert.True(inside.Count == 0, $"{prefab} at {at} is inside {string.Join(", ", inside)}");
        }
        // Taps start shut; the downpipes run with the rain.
        foreach (var tap in new[] { "kitchen_sink_water", "dripping_sink_water", "washbasin_water", "shower_water" })
        {
            Assert.False(prefabs[tap].GetProperty("SynthRunning").GetBoolean());
            Assert.NotNull(RunningWaterSpec.ByName(prefabs[tap].GetProperty("SoundId").GetString()![5..]).Tap);
        }
    }
}

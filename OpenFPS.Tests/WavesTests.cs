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
/// Waves at an edge (docs/WAVES_AND_SHORES.md): the sea the wind raises over a fetch, how a wave breaks
/// and runs up, the shore source's geometry on a map, and the synth's sound against recorded shores.
/// </summary>
public class WavesTests
{
    private readonly ITestOutputHelper _o;
    public WavesTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;

    private static double Db(double meanSquare) => 10 * Math.Log10(Math.Max(1e-30, meanSquare) / 4e-10);
    private static double MeanSquare(float[] x) => x.Sum(v => (double)v * v) / x.Length;

    private static float[] Render(ShoreSpec spec, float wind, float seconds, int seed = 3, float fromDegrees = 0f, ShoreGeometry? geometry = null, float spread = 0f)
    {
        var s = new ShoreSynth(spec, Rate, seed, geometry) { WindSpeed = wind, WindFromDegrees = fromDegrees, Spread = spread };
        float settle = spec.BreakRowMetres > 0f ? 25f : 5f;
        for (int i = 0; i < Rate * settle; i++) { if (i % 256 == 0) s.Control(256f / Rate); s.Next(); }
        var x = new float[(int)(seconds * Rate)];
        for (int i = 0; i < x.Length; i++)
        {
            if (i % 256 == 0) s.Control(256f / Rate);
            x[i] = s.Next();
        }
        return x;
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    // ── The sea ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_wind_raises_the_sea_JONSWAP_says()
    {
        // χ = g F / U² = 392.4 for 5 m/s over a kilometre: Hm0 = 1.6e-3 √χ U² / g, Tp = 0.286 χ^0.33 U / g.
        var (h, t) = WindWaves.FetchLimited(5f, 1000f);
        Assert.InRange(h, 0.079f, 0.082f);
        Assert.InRange(t, 1.03f, 1.07f);
        // More wind, more fetch: higher and longer.
        var (h2, t2) = WindWaves.FetchLimited(10f, 1000f);
        var (h3, t3) = WindWaves.FetchLimited(5f, 10000f);
        Assert.True(h2 > 1.8f * h && t2 > t);
        Assert.True(h3 > 3f * h && t3 > 2f * t);
        // Never past the fully developed sea (CEM II-2-37): g Hm0 / u*² = 211.5.
        float us = WindWaves.FrictionVelocity(10f);
        Assert.True(WindWaves.FetchLimited(10f, 1e7f).Hm0 <= 211.5f * us * us / WindWaves.Gravity + 1e-3f);
        // Shallow water holds it down.
        Assert.True(WindWaves.FetchLimited(15f, 20000f, 1f).Hm0 < 0.6f * WindWaves.FetchLimited(15f, 20000f).Hm0);
        // Glassy under the onset.
        Assert.Equal(0f, WindWaves.FetchLimited(1f, 1000f).Hm0);
    }

    [Fact]
    public void A_sea_takes_the_CEMs_time_to_grow()
    {
        // t = 77.23 F^0.67 / (U^0.34 g^0.33): about 36 min for a kilometre at 5 m/s.
        float t = WindWaves.GrowthSeconds(5f, 1000f);
        Assert.InRange(t, 2100f, 2200f);
        Assert.True(WindWaves.GrowthSeconds(5f, 150f) < 0.4f * t);
    }

    [Fact]
    public void Waves_break_by_their_Iribarren_number()
    {
        // A metre of 9 s swell on a 1 in 30 beach spills or plunges; the same on 1 in 6 shingle plunges;
        // a lake's chop on a 1 in 12 beach spills; a long low swell on a steep wall surges.
        Assert.InRange(WindWaves.Iribarren(1f / 30f, 1f, 9f), 0.3f, 0.5f);
        Assert.InRange(WindWaves.Iribarren(1f / 6f, 0.6f, 7f), 0.5f, 3.3f);
        Assert.True(WindWaves.Iribarren(1f / 12f, 0.08f, 1.05f) < 0.5f);
        Assert.True(WindWaves.Iribarren(1f, 0.2f, 10f) > 3.3f);
        // Hunt's run-up, R = ξ H while it breaks.
        float xi = WindWaves.Iribarren(0.1f, 0.5f, 6f);
        Assert.Equal(xi * 0.5f, WindWaves.RunUp(0.1f, 0.5f, 6f), 3);
    }

    [Fact]
    public void A_young_steep_sea_breaks_in_whitecaps_and_a_calm_one_does_not()
    {
        var (h, t) = WindWaves.FetchLimited(8f, 2000f);
        Assert.True(WindWaves.BreakingProbability(h, t, 8f) > 0.05f);
        var (h2, t2) = WindWaves.FetchLimited(2.5f, 2000f);
        Assert.Equal(0f, WindWaves.BreakingProbability(h2, t2, 2.5f));
    }

    [Fact]
    public void The_lee_shore_is_still_and_the_shore_the_wind_blows_onto_laps()
    {
        var spec = ShoreSpec.LakeSand;
        var geo = new ShoreGeometry(500f, 90f, 20f);           // the water lies to the east
        var onshore = spec.WindSea(geo, 6f, 90f);                // wind from the east, over the water
        var offshore = spec.WindSea(geo, 6f, 270f);              // wind from the west, off the land
        var slant = spec.WindSea(geo, 6f, 150f);                 // 60 degrees off
        Assert.True(onshore.Hs > 0.05f);
        Assert.Equal(0f, offshore.Hs);
        Assert.InRange(slant.Hs, 0.2f * onshore.Hs, 0.8f * onshore.Hs);
        // A river's bank: a slantwise wind crosses more water, but the bank meets its waves at a slant.
        var river = ShoreSpec.RiverBank;
        var rg = new ShoreGeometry(120f, 0f, 20f);
        Assert.True(river.WindSea(rg, 6f, 60f).Tp > river.WindSea(rg, 6f, 0f).Tp);
    }

    // ── The map's key ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_shore_source_carries_its_geometry_in_its_key()
    {
        // A stretch 18 m long, 140 m of water straight out to the east: turned so its +Z faces east.
        var rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        string key = ShoreSpec.KeyFor("shore:pond_bank", new Vector3(18f, 0.2f, 140f), rot);
        Assert.Equal("shore:pond_bank/140/90/18", key);
        Assert.True(ShoreSpec.ParseKey(key, out string preset, out var g));
        Assert.Equal("pond_bank", preset);
        Assert.Equal(new ShoreGeometry(140f, 90f, 18f), g);
        Assert.Equal(ShoreSpec.PondBank.Name, ShoreSpec.ByName(key).Name);
        // The prefab's own key names the preset, with no geometry.
        Assert.True(ShoreSpec.ParseKey("shore:sea_sand", out preset, out g));
        Assert.Equal("sea_sand", preset);
        Assert.Null(g);
        // Its places lie along it, the stretch's own length, and a surf beach has a row on its break line.
        var layout = ExtendedSources.Layout(key)!;
        Assert.Equal(ShoreSpec.PondBank.TotalPlaces, layout.Length);
        Assert.True(layout.Max(p => MathF.Abs(p.X)) <= 9f && layout.All(p => p.Z == 0f));
        var surf = ExtendedSources.Layout("shore:sea_sand")!;
        Assert.Equal(2 * ShoreSpec.SeaSand.Places, surf.Length);
        Assert.Contains(surf, p => p.Z == ShoreSpec.SeaSand.BreakRowMetres);
    }

    [Fact]
    public void Every_preset_round_trips_through_the_model_library()
    {
        foreach (var id in ShoreSpec.Presets.Keys)
            Assert.True(ModelLibrary.RoundTrips(ModelLibrary.Kinds.Shore, ModelLibrary.Shore(id), out _, out _), id);
    }

    // ── The sound ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Still_water_is_silent_and_a_breeze_makes_it_lap()
    {
        var pond = ShoreSpec.PondBank;
        Assert.True(pond.CalmAt(1f));
        var calm = Render(pond, 0.5f, 4f);
        Assert.Equal(0.0, MeanSquare(calm));
        var breeze = Render(pond, 6f, 8f);
        Assert.True(Db(MeanSquare(breeze)) > 30.0);
        // A river's bank laps in a calm: the current's eddies. The sea's swell comes whatever the wind.
        Assert.False(ShoreSpec.RiverBank.CalmAt(0f));
        Assert.True(Db(MeanSquare(Render(ShoreSpec.RiverBank, 0f, 8f))) > 20.0);
        Assert.True(Db(MeanSquare(Render(ShoreSpec.Shingle, 0f, 10f))) > 50.0);
    }

    /// <summary>
    /// A shore is never silent, so a shore voice starts mid-sea: waves already arriving and the last
    /// ones' swash still running, from its first moment. It used to start from a still sea and wait for
    /// its first wave, two up-crossings of the surface: a sandy beach under a 9 s swell was exact silence
    /// for about 11 s after it won a voice (docs/COVERAGE_2026-10-06.md, finding 2). Through the game's
    /// own path (PlacedNatureVoice, its control from the held weather), for several seeds: the first
    /// sample sounds, and no second of the first fifteen is far under the sea's own level later on.
    /// </summary>
    [Fact]
    public void A_shore_voice_is_heard_from_its_first_moment()
    {
        using var wind = WindField.Hold(WindWeather.Steady(4.5f, 270f, 0f));
        var spec = ShoreSpec.SeaSand;
        const int Seconds = 40;
        foreach (int seed in new[] { 23, 2 })
        {
            var voice = new OpenFPS.Client.AudioEngine.Fmod.PlacedNatureVoice("shore:sea", spec, spec.DefaultGeometry, Rate, seed, Vector3.Zero);
            // The first block is where the sea is got under way: what that costs the render thread once.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            voice.Sample(0, 0);
            double firstMs = clock.Elapsed.TotalMilliseconds;
            var perSecond = new double[Seconds];
            long firstSound = -1;
            for (long at = 0; at < (long)Seconds * Rate; at++)
            {
                double y = 0;
                for (int p = 0; p < voice.Places; p++) y += voice.Sample(p, at);
                if (firstSound < 0 && y != 0) firstSound = at;
                perSecond[at / Rate] += y * y / Rate;
            }
            var later = perSecond.Skip(20).Select(Db).OrderBy(v => v).ToArray();
            double median = later[later.Length / 2], quietLater = later[0];
            var first = perSecond.Take(15).Select(Db).ToArray();
            _o.WriteLine($"seed {seed}: first sound at {firstSound / (double)Rate * 1000:F1} ms; seconds 0-14 " +
                         string.Join(" ", first.Select(v => v.ToString("F0"))) +
                         $"; seconds 20-39 median {median:F1}, quietest {quietLater:F1} dB; first 512 samples rendered in {firstMs:F1} ms");
            Assert.InRange(firstSound, 0, Rate / 100);
            Assert.All(first, v => Assert.True(v > quietLater - 6.0,
                $"seed {seed}: a second of the start at {v:F1} dB, under the quietest second later ({quietLater:F1}) by more than 6 dB"));
        }
    }

    [Fact]
    public void More_wind_makes_more_noise()
    {
        var spec = ShoreSpec.LakeSand;
        double light = Db(MeanSquare(Render(spec, 3f, 10f)));
        double fresh = Db(MeanSquare(Render(spec, 8f, 10f)));
        _o.WriteLine($"lake beach: 3 m/s {light:F1} dB, 8 m/s {fresh:F1} dB at a metre");
        Assert.True(fresh > light + 6.0);
    }

    [Fact]
    public void A_hull_rings_lower_wet_and_aluminium_rings_lower_and_longer_than_wood()
    {
        var wood = ShoreSpec.HullWood.Hull!;
        var alu = ShoreSpec.HullAluminium.Hull!;
        for (int m = 1; m <= 2; m++)
            for (int n = 1; n <= 2; n++)
            {
                Assert.True(wood.WetModeHz(m, n) < wood.Plate.ModeHz(m, n));
                Assert.True(alu.WetModeHz(m, n) < alu.Plate.ModeHz(m, n));
            }
        Assert.True(alu.WetModeHz(1, 1) < 0.5f * wood.WetModeHz(1, 1));
        Assert.True(alu.Plate.StructuralLoss < wood.Plate.StructuralLoss);
    }

    [Fact]
    public void Every_preset_renders_clean_sound()
    {
        foreach (var (id, make) in ShoreSpec.Presets)
        {
            var spec = make();
            var x = Render(spec, spec.ReferenceWind, 4f, seed: 5);
            Assert.All(x, v => Assert.True(float.IsFinite(v)));
            double ms = MeanSquare(x);
            double peak = x.Max(v => MathF.Abs(v));
            _o.WriteLine($"{id}: {Db(ms):F1} dB, peak {20 * Math.Log10(peak / Math.Sqrt(ms)):F1} dB over the rms");
            Assert.True(ms > 0, id);
        }
    }

    [Fact]
    public void The_places_add_up_to_the_source_and_are_not_copies()
    {
        var spec = ShoreSpec.LakeSand;
        var s = new ShoreSynth(spec, Rate, 9) { WindSpeed = 6f, WindFromDegrees = 0f, Spread = 1f };
        var places = new float[spec.TotalPlaces];
        var sums = new double[spec.TotalPlaces];
        double cross = 0;
        for (int i = 0; i < Rate * 10; i++)
        {
            if (i % 256 == 0) s.Control(256f / Rate);
            s.NextPlaces(places);
            for (int k = 0; k < places.Length; k++) sums[k] += places[k] * (double)places[k];
            cross += places[1] * (double)places[2];
        }
        Assert.All(sums, v => Assert.True(v > 0));
        double corr = cross / Math.Sqrt(sums[1] * sums[2]);
        _o.WriteLine($"places 1 and 2 correlate {corr:F3}");
        Assert.InRange(corr, -0.1, 0.1);
    }

    // ── On the maps ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Albany and Magnolia have their ponds' edges as stretches of shore (tools/gen_osm.py): each a model
    /// this client knows, its box a stretch of about 20 m and a fetch of at least a metre, its +Z turned to
    /// the water (ten metres out from most of them is a water surface, behind none of them).
    /// </summary>
    [Theory]
    [InlineData("albany_or", 200)]
    [InlineData("magnolia_tx", 30)]
    public void A_real_places_ponds_have_their_shores(string id, int atLeast)
    {
        string root = RepoRoot();
        var prefabs = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(Path.Combine(root, "OpenFPS.Server", "prefabs"), "shore_*.json"))
        {
            var doc = JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
            prefabs[doc.GetProperty("Id").GetString()!] = doc;
        }
        using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "OpenFPS.Server", "maps", "places", id + ".json")));
        static Vector3 V(JsonElement e) => new(e.GetProperty("X").GetSingle(), e.GetProperty("Y").GetSingle(), e.GetProperty("Z").GetSingle());
        var water = new List<(Vector3 At, Quaternion Rot, Vector3 Half)>();
        var shores = new List<(string Prefab, Vector3 At, Quaternion Rot, Vector3 Scale)>();
        foreach (var e in map.RootElement.GetProperty("Entities").EnumerateArray())
        {
            string prefab = e.GetProperty("PrefabId").GetString()!;
            var at = V(e.GetProperty("Position"));
            var rot = e.TryGetProperty("Rotation", out var r)
                ? new Quaternion(r.GetProperty("X").GetSingle(), r.GetProperty("Y").GetSingle(), r.GetProperty("Z").GetSingle(), r.GetProperty("W").GetSingle())
                : Quaternion.Identity;
            var scale = e.TryGetProperty("Scale", out var sc) ? V(sc) : Vector3.One;
            if (prefab == "water_surface") water.Add((at, rot, new Vector3(4f, 0.1f, 4f) * scale / 2f));
            else if (prefab.StartsWith("shore_", StringComparison.Ordinal)) shores.Add((prefab, at, rot, scale));
        }
        _o.WriteLine($"{id}: {shores.Count} stretches of shore, {water.Count} water boxes");
        Assert.True(shores.Count >= atLeast);
        // The water boxes cover a pond in 8 m cells inside its outline, so a stretch's water is looked for
        // ten metres out; its land side must never be water.
        bool InWater(Vector3 p) => water.Any(w =>
        {
            var local = Vector3.Transform(p - w.At, Quaternion.Inverse(w.Rot));
            return MathF.Abs(local.X) <= w.Half.X + 0.5f && MathF.Abs(local.Z) <= w.Half.Z + 0.5f;
        });
        int overWater = 0, overLand = 0;
        foreach (var (prefab, at, rot, scale) in shores)
        {
            Assert.True(prefabs.ContainsKey(prefab), prefab);
            string sound = prefabs[prefab].GetProperty("SoundId").GetString()!;
            Assert.True(ModelLibrary.Knows(ModelLibrary.Kinds.Shore, sound[6..]), sound);
            Assert.InRange(scale.X, 5f, 30f);
            Assert.True(scale.Z >= 1f);
            if (InWater(at + Vector3.Transform(new Vector3(0f, 0f, 10f), rot))) overWater++;
            if (InWater(at + Vector3.Transform(new Vector3(0f, 0f, -10f), rot))) overLand++;
        }
        _o.WriteLine($"  {overWater} of {shores.Count} have water 10 m out on their +Z, {overLand} behind them");
        Assert.True(overWater >= 0.6 * shores.Count);
        Assert.Equal(0, overLand);
    }
}

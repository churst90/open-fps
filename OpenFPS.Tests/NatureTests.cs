using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using OpenFPS.Common;
using OpenFPS.Client.AudioEngine.Core.Nature;
using OpenFPS.Client.AudioEngine.Fmod;
using Xunit;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Water, fire and the wind in leaves: the wind field they share, the physics each is built on, the
/// level each voice arrives at the mixer with, and where they stand on the city.
///
/// The models were fitted against recordings with the lab (--nature levels / compare=); these hold
/// the things that must not drift once fitted — a gust that travels, a bubble that rings at its
/// Minnaert note, a voice at its declared level with room for its peaks, and a fountain that is not
/// inside its own pedestal.
/// </summary>
public class NatureTests
{
    private readonly ITestOutputHelper _o;
    public NatureTests(ITestOutputHelper o)
    {
        _o = o;
        AcousticRegistry.Initialize();
    }

    private const int Rate = 48000;

    // ── The wind ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWindIsTheSameForEveryoneWhoAsks()
    {
        for (double t = 1000; t < 1100; t += 7.3)
            Assert.Equal(WindField.SpeedAt(12f, 7f, -40f, t), WindField.SpeedAt(12f, 7f, -40f, t));
    }

    [Fact]
    public void TheGustSignalHasUnitVarianceAboutZero()
    {
        double sum = 0, sq = 0;
        int n = 0;
        for (double t = 0; t < 40000; t += 0.7) { float g = WindField.Gust(-300f, 200f, t); sum += g; sq += g * g; n++; }
        double mean = sum / n, sd = Math.Sqrt(sq / n - mean * mean);
        _o.WriteLine($"gust mean {mean:F3}, sd {sd:F3}");
        Assert.InRange(mean, -0.15, 0.15);
        Assert.InRange(sd, 0.8, 1.2);
    }

    /// <summary>
    /// A gust is carried by the wind: what a place downwind feels is what a place upwind felt, the
    /// distance over the wind speed earlier. That is what makes a gust heard coming through a park.
    /// </summary>
    [Fact]
    public void AGustTravelsDownwind()
    {
        var (dx, dz) = WindField.Downwind;
        const float metres = 90f;
        float lag = metres / WindField.MeanSpeed;
        double same = 0, carried = 0, aa = 0, bb = 0, cc = 0;
        for (double t = 0; t < 6000; t += 0.5)
        {
            float a = WindField.Gust(0f, 0f, t);
            float b = WindField.Gust(dx * metres, dz * metres, t);
            float c = WindField.Gust(dx * metres, dz * metres, t + lag);
            same += a * b; carried += a * c; aa += a * a; bb += b * b; cc += c * c;
        }
        double rSame = same / Math.Sqrt(aa * bb), rCarried = carried / Math.Sqrt(aa * cc);
        _o.WriteLine($"correlation 90 m downwind: at once {rSame:F2}, {lag:F0} s later {rCarried:F2}");
        Assert.True(rCarried > 0.98, $"the gust downwind {lag:F0} s later should be the same gust: {rCarried:F2}");
        Assert.True(rSame < rCarried - 0.1, $"at the same moment it should not be: {rSame:F2}");
    }

    [Fact]
    public void TheWindFallsOffTowardTheGround()
    {
        Assert.True(WindField.MeanAt(0.8f) < WindField.MeanAt(7f));
        Assert.True(WindField.MeanAt(7f) < WindField.MeanAt(10f) + 1e-4f);
        Assert.Equal(WindField.MeanSpeed, WindField.MeanAt(10f), 3);
    }

    // ── Water ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ABubbleRingsAtItsMinnaertNoteAndDiesAsVanDenDoelSays()
    {
        // 3.26 / R: a millimetre is 3.26 kHz.
        Assert.Equal(3260f, FallingWaterSynth.MinnaertHzMetres / 1e-3f, 1);
        // d = 0.13 / R + 0.0072 R^-3/2: 130 + 228 per second at a millimetre, a damping ratio of 0.035.
        Assert.InRange(FallingWaterSynth.BubbleDecay(1f), 350f, 365f);
        Assert.InRange(FallingWaterSynth.BubbleDamping(1f), 0.03f, 0.04f);
        // Medwin's type II bubble: about 10 kHz from a 2.2 mm drop, falling for bigger ones.
        float small = FallingWaterSynth.MinnaertHzMetres / (FallingWaterSynth.TypeTwoBubbleMm(1.1f) * 1e-3f);
        float big = FallingWaterSynth.MinnaertHzMetres / (FallingWaterSynth.TypeTwoBubbleMm(3f) * 1e-3f);
        Assert.InRange(small, 9000f, 11000f);
        Assert.InRange(big, 1500f, 3000f);
    }

    [Fact]
    public void ADropFallsNoFasterThanItsTerminalSpeed()
    {
        // Gunn and Kinzer measured 6.49 m/s for a 2 mm drop; the fit gives 6.55.
        Assert.InRange(FallingWaterSynth.TerminalSpeed(1e-3f), 6.3f, 6.7f);
        Assert.True(FallingWaterSynth.ArrivalSpeed(1e-3f, 100f) <= FallingWaterSynth.TerminalSpeed(1e-3f) + 1e-3f);
        // A short fall is still nearly free fall: √(2 g h).
        Assert.InRange(FallingWaterSynth.ArrivalSpeed(2e-3f, 0.3f), 2.2f, 2.45f);
    }

    [Fact]
    public void TwoFountainsAreTwoFountains()
    {
        var spec = WaterFeatureSpec.ByName("park_fountain");
        var a = new FallingWaterSynth(spec, Rate, 1);
        var b = new FallingWaterSynth(spec, Rate, 2);
        double ab = 0, aa = 0, bb = 0;
        for (int i = 0; i < Rate * 2; i++)
        {
            if (i % 256 == 0) { a.Control(256f / Rate); b.Control(256f / Rate); }
            float x = a.Next(), y = b.Next();
            ab += x * y; aa += x * x; bb += y * y;
        }
        double r = ab / Math.Sqrt(aa * bb);
        Assert.InRange(r, -0.1, 0.1);
    }

    // ── The voices ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A voice rendered offline, its ring undone into pascals: rms dB, and how much of it sat
    /// on the soft ceiling's knee. In blocks, as the mixer takes it: one long Render would run the
    /// voice's ring round on itself.</summary>
    private static (float RmsDb, double OnKnee) Measure(PhysicalVoiceState voice, float seconds)
    {
        var warm = new float[Rate / 2];
        for (int i = 0; i < warm.Length; i += 1024) voice.Render(warm.AsSpan(i, Math.Min(1024, warm.Length - i)));
        var buf = new float[(int)(Rate * seconds)];
        for (int i = 0; i < buf.Length; i += 1024) voice.Render(buf.AsSpan(i, Math.Min(1024, buf.Length - i)));
        double sum = 0;
        int knee = 0;
        foreach (float v in buf)
        {
            float pa = v * voice.PascalsAtFullScale;
            sum += (double)pa * pa;
            if (MathF.Abs(v) > SoftCeiling.Knee) knee++;
        }
        float rms = MathF.Sqrt((float)(sum / buf.Length));
        return (20f * MathF.Log10(MathF.Max(rms, 1e-9f) / 20e-6f), knee / (double)buf.Length);
    }

    /// <summary>
    /// The fountain and the fire reach the mixer at the level they declare, and the fire's crackles,
    /// forty decibels over its mean, fit inside its own headroom rather than being squared off.
    /// Three decibels of tolerance: a fire has its quiet spells and its busy ones.
    /// </summary>
    [Fact]
    public void WaterAndFireRenderAtTheLevelTheyDeclare()
    {
        var water = WaterFeatureSpec.ByName("park_fountain");
        var (wDb, wKnee) = Measure(new WaterVoiceState(water, Rate, 5, Vector3.Zero), 8f);
        _o.WriteLine($"fountain {wDb:F1} dB against {water.SourceLevelDb:F1}, {wKnee:P3} on the knee");
        Assert.InRange(wDb, water.SourceLevelDb - 3f, water.SourceLevelDb + 3f);
        Assert.True(wKnee < 1e-4, $"the fountain sat on the soft ceiling {wKnee:P3} of the time");

        var fire = FireSpec.ByName("fire_pit");
        var (fDb, fKnee) = Measure(new FireVoiceState(fire, Rate, 5, Vector3.Zero), 30f);
        _o.WriteLine($"fire {fDb:F1} dB against {fire.SourceLevelDb:F1}, {fKnee:P4} on the knee");
        Assert.InRange(fDb, fire.SourceLevelDb - 3f, fire.SourceLevelDb + 3f);
        Assert.True(fKnee < 1e-4, $"the fire sat on the soft ceiling {fKnee:P4} of the time");
    }

    /// <summary>The headroom a nature voice renders with is given back by the channel, so placing a
    /// fire by its mean is not undone by giving it room for its crackles.</summary>
    [Fact]
    public void ExtraHeadroomIsGivenBackAsGain()
    {
        Assert.Equal(1f, PhysicalVoiceState.HeadroomGain(VehicleProfile.PeakHeadroomDb), 4);
        Assert.Equal(1f, PhysicalVoiceState.HeadroomGain(10f), 4);
        float fire = FireSpec.ByName("fire_pit").PeakHeadroomDb;
        Assert.Equal(MathF.Pow(10f, (fire - VehicleProfile.PeakHeadroomDb) / 20f), PhysicalVoiceState.HeadroomGain(fire), 3);
    }

    /// <summary>A fire puffs at the rate its width sets, about 1.5 / √D (Cetegen and Ahmed 1993).</summary>
    [Fact]
    public void AFirePuffsAtTheRateItsWidthSets()
    {
        var spec = FireSpec.ByName("fire_pit");
        var fire = new FireSynth(spec, Rate, 3) { CracklePart = 0f, SteamPart = 0f, SettlePart = 0f };
        int hop = Rate / 50;
        var env = new List<double>();
        double e = 0;
        for (int i = 0; i < Rate * 60; i++)
        {
            if (i % 256 == 0) fire.Control(256f / Rate);
            float y = fire.Next();
            e += y * y;
            if ((i + 1) % hop == 0) { env.Add(Math.Sqrt(e / hop)); e = 0; }
        }
        double mean = env.Average(), best = 0, bestHz = 0;
        for (double hz = 0.5; hz <= 5; hz *= 1.02)
        {
            double re = 0, im = 0;
            for (int i = 0; i < env.Count; i++)
            {
                double ph = 2 * Math.PI * hz * i / 50.0;
                re += (env[i] - mean) * Math.Cos(ph);
                im += (env[i] - mean) * Math.Sin(ph);
            }
            double mag = re * re + im * im;
            if (mag > best) { best = mag; bestHz = hz; }
        }
        double expected = 1.5 / Math.Sqrt(spec.BaseDiameterMetres);
        _o.WriteLine($"puffing at {bestHz:F2} Hz, expected {expected:F2}");
        Assert.InRange(bestHz, expected * 0.75, expected * 1.25);
    }

    [Fact]
    public void WetterWoodCracklesMore()
    {
        var dry = new FireSynth(FireSpec.ByName("fire_pit") with { Moisture = 0.12f }, Rate, 1);
        var wet = new FireSynth(FireSpec.ByName("fire_pit") with { Moisture = 0.35f }, Rate, 1);
        Assert.True(wet.CrackleRate > 1.5f * dry.CrackleRate);
    }

    /// <summary>
    /// The rustle grows with the wind faster than the wind does: Fégeant's vegetation noise rises 30
    /// (oak) to 36 (birch) dB a decade of wind speed. From 3 to 6 m/s, steady, that is 9 to 11 dB,
    /// and a little more here for the leaves that are still at 3 and moving at 6.
    /// </summary>
    [Fact]
    public void TheRustleGrowsWithTheWindAsFegeantMeasured()
    {
        float Level(float wind)
        {
            var tree = new FoliageSynth(FoliageSpec.ByName("park_tree"), Rate, 9) { Wind = wind };
            double sum = 0;
            int n = 0;
            for (int i = 0; i < Rate * 12; i++)
            {
                if (i % 256 == 0) tree.Control(256f / Rate);
                float y = tree.Next();
                if (i < Rate * 2) continue;
                sum += y * y; n++;
            }
            return 10f * MathF.Log10((float)(sum / n));
        }
        float rise = Level(6f) - Level(3f);
        _o.WriteLine($"3 to 6 m/s: {rise:F1} dB");
        Assert.InRange(rise, 8f, 14f);
    }

    [Fact]
    public void APineSighsAndDoesNotRustle()
    {
        var pine = new FoliageSynth(FoliageSpec.ByName("pine"), Rate, 1);
        Assert.Equal(0f, pine.StrikesPerSecond(5f));
        var lime = new FoliageSynth(FoliageSpec.ByName("park_tree"), Rate, 1);
        Assert.True(lime.StrikesPerSecond(5f) > 1000f);
        Assert.Equal(0f, lime.StrikesPerSecond(0.5f));
    }

    // ── On the city ────────────────────────────────────────────────────────────────────────────

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    private sealed record Box(string Prefab, string? Name, Vector3 Centre, Vector3 Half, bool Solid);

    private static (List<Box> Boxes, Dictionary<string, JsonElement> Prefabs) City()
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
        var boxes = new List<Box>();
        foreach (var e in map.GetProperty("Entities").EnumerateArray())
        {
            string id = e.GetProperty("PrefabId").GetString()!;
            if (!prefabs.TryGetValue(id, out var p)) continue;
            var c = p.TryGetProperty("ColliderSize", out var cs)
                ? new Vector3(cs.GetProperty("X").GetSingle(), cs.GetProperty("Y").GetSingle(), cs.GetProperty("Z").GetSingle())
                : Vector3.One;
            var s = e.TryGetProperty("Scale", out var sc)
                ? new Vector3(sc.GetProperty("X").GetSingle(), sc.GetProperty("Y").GetSingle(), sc.GetProperty("Z").GetSingle())
                : Vector3.One;
            var pos = e.GetProperty("Position");
            // Missing IsSolid is solid, as the server reads it.
            bool solid = !p.TryGetProperty("IsSolid", out var so) || so.GetBoolean();
            string? name = e.TryGetProperty("Name", out var nm) ? nm.GetString() : null;
            boxes.Add(new Box(id, name, new Vector3(pos.GetProperty("X").GetSingle(), pos.GetProperty("Y").GetSingle(), pos.GetProperty("Z").GetSingle()),
                              c * s / 2f, solid));
        }
        return (boxes, prefabs);
    }

    private static bool Inside(Box b, Vector3 p)
        => MathF.Abs(p.X - b.Centre.X) < b.Half.X && MathF.Abs(p.Y - b.Centre.Y) < b.Half.Y && MathF.Abs(p.Z - b.Centre.Z) < b.Half.Z;

    /// <summary>
    /// The park has its fountain and its trees, the garden its fire, and every one of them names a
    /// model the client knows. And none of them is inside anything solid: a source inside a box is
    /// heard through the box, which is how the first draft put the fountain inside its own pedestal.
    /// </summary>
    [Fact]
    public void TheParkAndTheGardenFireAreOnTheCity()
    {
        var (boxes, prefabs) = City();
        var sources = boxes.Where(b => b.Prefab is "water_fountain" or "fire_pit" or "tree_crown").ToList();
        Assert.Single(sources, b => b.Prefab == "water_fountain");
        Assert.Single(sources, b => b.Prefab == "fire_pit");
        Assert.True(sources.Count(b => b.Prefab == "tree_crown") >= 10);

        foreach (var src in sources)
        {
            string sound = prefabs[src.Prefab].GetProperty("SoundId").GetString()!;
            string kind = sound[..sound.IndexOf(':')], key = sound[(sound.IndexOf(':') + 1)..];
            object model = kind switch
            {
                "water" => WaterFeatureSpec.ByName(key),
                "fire" => FireSpec.ByName(key),
                "foliage" => FoliageSpec.ByName(key),
                _ => throw new Xunit.Sdk.XunitException($"{src.Prefab} names '{sound}', which is no nature model"),
            };
            Assert.NotNull(model);
            var walls = boxes.Where(b => b.Solid && Inside(b, src.Centre)).Select(b => b.Name ?? b.Prefab).ToList();
            Assert.True(walls.Count == 0, $"{src.Name} at {src.Centre} is inside {string.Join(", ", walls)}");
        }
    }

    /// <summary>The fire is in a garden fenced on three sides with a gate, and the house is the fourth.</summary>
    [Fact]
    public void TheFireIsInAFencedGarden()
    {
        var (boxes, _) = City();
        var fire = boxes.Single(b => b.Prefab == "fire_pit");
        var fences = boxes.Where(b => b.Prefab == "fence_timber").ToList();
        Assert.True(fences.Count >= 4);
        // A fence each side of it, east and west, and one to the north.
        Assert.Contains(fences, f => f.Centre.X < fire.Centre.X && MathF.Abs(f.Centre.Z - fire.Centre.Z) < f.Half.Z);
        Assert.Contains(fences, f => f.Centre.X > fire.Centre.X && MathF.Abs(f.Centre.Z - fire.Centre.Z) < f.Half.Z);
        Assert.Contains(fences, f => f.Centre.Z > fire.Centre.Z && f.Half.X > 2f);
        // ...and the garden it is in has a name.
        Assert.Contains(boxes, b => b.Prefab == "acoustic_region" && b.Name != null && b.Name.EndsWith("back garden") && Inside(b, fire.Centre with { Y = 1f }));
    }
}

using System.Numerics;
using System.Text.Json;
using OpenFPS.Common;
using Xunit.Abstractions;

namespace OpenFPS.Tests;

/// <summary>
/// Which glass a part is (GlassKind), over the city's own glass, and that the glass model scales with the pane.
/// </summary>
public class GlassKindTests
{
    private readonly ITestOutputHelper _out;
    public GlassKindTests(ITestOutputHelper output) { _out = output; AcousticRegistry.Initialize(); }

    /// <summary>Each glass prefab and the glazing it declares; a glass prefab that declares none is missing here.</summary>
    private static Dictionary<string, GlassType?> GlassPrefabs()
    {
        var glass = new Dictionary<string, GlassType?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "prefabs"), "*.json"))
        {
            if (path.EndsWith("prefab-schema.json")) continue;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var p = doc.RootElement;
            if (p.TryGetProperty("Material", out var m) && AcousticRegistry.IsGlass(m.GetString()))
                glass[p.GetProperty("Id").GetString()!] = p.TryGetProperty("Glazing", out var g) ? Enum.Parse<GlassType>(g.GetString()!) : null;
        }
        return glass;
    }

    /// <summary>Every prefab of glass says what kind of glass it is.</summary>
    [Fact]
    public void EveryGlassPrefabDeclaresItsGlazing()
    {
        var glass = GlassPrefabs();
        Assert.True(glass.Count >= 5, string.Join(", ", glass.Keys));
        Assert.All(glass, g => Assert.True(g.Value != null, $"{g.Key} declares no Glazing"));
    }

    /// <summary>
    /// The rule the glass followed before it was declared (2026-10-11): door prefabs tempered, a name with "window" or
    /// "skylight" annealed, everything else tempered. Kept here only to show that declaring it changed no pane on any
    /// shipped map.
    /// </summary>
    private static GlassType ByTheOldNameRule(string prefab, string name)
    {
        if (prefab is "glass_front_door" or "glass_pull_door" or "auto_sliding_door" or "patio_door") return GlassType.Tempered;
        string n = name.ToLowerInvariant();
        return n.Contains("window") || n.Contains("skylight") ? GlassType.Annealed : GlassType.Tempered;
    }

    /// <summary>On every shipped map each pane is the glass its prefab declares, and that is what its name made it
    /// before: nothing breaks differently.</summary>
    [Theory]
    [InlineData("maps", "city")]
    [InlineData("maps", "speedway")]
    [InlineData("places", "albany_or")]
    [InlineData("places", "magnolia_tx")]
    public void EveryShippedPaneIsTheGlassItWas(string folder, string map)
    {
        var glass = GlassPrefabs();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, folder, map + ".json")));
        int panes = 0;
        foreach (var e in doc.RootElement.GetProperty("Entities").EnumerateArray())
        {
            string prefab = e.GetProperty("PrefabId").GetString()!;
            if (!glass.TryGetValue(prefab, out var declared)) continue;
            string name = e.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            panes++;
            Assert.Equal(ByTheOldNameRule(prefab, name), GlassKind.Of(new GlazedPart(prefab, declared, "", Vector3.Zero, Vector3.One)));
        }
        _out.WriteLine($"{map}: {panes} panes");
        Assert.DoesNotContain(GlassKind.Undeclared, u => glass.ContainsKey(u));
    }

    /// <summary>Every glazed part on the city map, and every vehicle placed on it, classified. The city has
    /// no house or flat windows as parts (its homes have patio doors), so nothing on it is annealed; every
    /// door, shelter and terminal panel is tempered, every car's side and back glass tempered, its windscreen
    /// laminated.</summary>
    [Fact]
    public void TheCitysGlassIsTheKindItsPartsAre()
    {
        var glassPrefabs = GlassPrefabs();
        using var map = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "maps", "city.json")));
        var counts = new Dictionary<(string, GlassType), int>();
        void Count(string what, GlassType t) => counts[(what, t)] = counts.GetValueOrDefault((what, t)) + 1;

        foreach (var e in map.RootElement.GetProperty("Entities").EnumerateArray())
        {
            string prefab = e.GetProperty("PrefabId").GetString()!;
            if (!glassPrefabs.TryGetValue(prefab, out var declared)) continue;
            Count(prefab, GlassKind.Of(new GlazedPart(prefab, declared, "", Vector3.Zero, Vector3.One)));
        }
        int vehicles = 0;
        foreach (var c in map.RootElement.GetProperty("Composites").EnumerateArray())
        {
            string template = c.GetProperty("TemplateId").GetString()!;
            if (!template.StartsWith("vehicle:", StringComparison.Ordinal)) continue;
            var v = MachineRegistry.VehicleFor(template.Substring(8));
            if (VehicleCabin.Measure(v) is not { } g) continue;
            vehicles++;
            foreach (var (part, at, size) in VehicleCabin.Shell(v, g))
                if (part == VehicleCabin.Glass)
                    Count(template, GlassKind.Of(new GlazedPart(part, null, template, at, size)));
        }
        foreach (var ((what, t), k) in counts.OrderBy(x => x.Key.Item1)) _out.WriteLine($"{what,-28} {t,-10} {k}");

        Assert.DoesNotContain(counts, x => x.Key.Item1 == "patio_door" && x.Key.Item2 != GlassType.Tempered);
        Assert.True(counts.GetValueOrDefault(("patio_door", GlassType.Tempered)) >= 60);
        Assert.Equal(11, counts.GetValueOrDefault(("glass_wall", GlassType.Tempered)));       // 6 shelter panels, 5 terminal bays
        Assert.Equal(5, counts.GetValueOrDefault(("glass_front_door", GlassType.Tempered)));
        Assert.Equal(4, counts.GetValueOrDefault(("auto_sliding_door", GlassType.Tempered)));
        Assert.DoesNotContain(counts, x => x.Key.Item2 == GlassType.Annealed);
        // Each car: one windscreen, laminated; its sides and back window tempered.
        Assert.Equal(vehicles, counts.Where(x => x.Key.Item2 == GlassType.Laminated).Sum(x => x.Value));
        Assert.Equal(vehicles * 3, counts.Where(x => x.Key.Item1.StartsWith("vehicle:") && x.Key.Item2 == GlassType.Tempered).Sum(x => x.Value));
    }

    /// <summary>Anything but a car's glass is the glazing declared, whatever it is called; a pane that declares none
    /// is said and breaks as tempered.</summary>
    [Theory]
    [InlineData(GlassType.Annealed)]
    [InlineData(GlassType.Tempered)]
    [InlineData(GlassType.Laminated)]
    public void APartIsTheGlassItsPrefabDeclares(GlassType declared)
    {
        Assert.Equal(declared, GlassKind.Of(new GlazedPart("glass_wall", declared, "", Vector3.Zero, Vector3.One)));
        Assert.Equal(GlassType.Tempered, GlassKind.Of(new GlazedPart("a_pane_of_nothing", null, "", Vector3.Zero, Vector3.One)));
        Assert.Contains("a_pane_of_nothing", GlassKind.Undeclared);
    }

    /// <summary>
    /// The model scales with the pane (Cody, 2026-10-04: "hopefully those models scale with window size"): a
    /// 0.6 x 0.9 m bathroom window and a 2 x 2.5 m shop front. Tempered glass dices by area, so the shop front
    /// makes as many more dice as it has more area; annealed glass breaks into more pieces over more area; and
    /// the bigger pane's own lowest mode is lower.
    /// </summary>
    [Fact]
    public void TheModelScalesWithThePane()
    {
        GlassFracture.Spec S(GlassType t, float w, float h) => new(GlassFracture.Part.Break, t, w, h, 0.006f,
            124f * WeaponRegistry.Grain, 375f, 1, 0.9f, "Concrete", 0);
        var smallT = GlassFracture.Census(S(GlassType.Tempered, 0.6f, 0.9f));
        var bigT = GlassFracture.Census(S(GlassType.Tempered, 2f, 2.5f));
        double areaRatio = (2.0 * 2.5) / (0.6 * 0.9);
        Assert.InRange(bigT.Dice / (double)smallT.Dice, areaRatio * 0.95, areaRatio * 1.05);

        var smallA = GlassFracture.Census(S(GlassType.Annealed, 0.6f, 0.9f));
        var bigA = GlassFracture.Census(S(GlassType.Annealed, 2f, 2.5f));
        int pieces(int shards, int slivers) => shards + slivers;
        Assert.True(pieces(bigA.Shards, bigA.Slivers) > 1.5 * pieces(smallA.Shards, smallA.Slivers),
                    $"annealed pieces: bathroom {pieces(smallA.Shards, smallA.Slivers)}, shop front {pieces(bigA.Shards, bigA.Slivers)}");

        double fSmall = GlassFracture.PaneFundamentalHz(S(GlassType.Annealed, 0.6f, 0.9f));
        double fBig = GlassFracture.PaneFundamentalHz(S(GlassType.Annealed, 2f, 2.5f));
        _out.WriteLine($"dice {smallT.Dice} / {bigT.Dice}; annealed pieces {pieces(smallA.Shards, smallA.Slivers)} / {pieces(bigA.Shards, bigA.Slivers)}; "
                     + $"fundamental {fSmall:F0} / {fBig:F0} Hz");
        Assert.True(fBig < fSmall / 4, $"fundamental: bathroom {fSmall:F0} Hz, shop front {fBig:F0} Hz");
    }
}

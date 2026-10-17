using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using OpenFPS.Common;
using Xunit.Abstractions;
using OpenFPS.Sound;

namespace OpenFPS.Tests;

/// <summary>
/// A material is told by what it is, never by its name (docs/ENGINE_PACKAGES.md 3.1, the four name tests; section 11,
/// "Names"): the place's name underfoot, a road's carriageway, a canopy, a window and a fence's pale each read a property
/// of the table,
/// so every material that has the property gets the behaviour and a new one is not left out because it is spelt otherwise.
/// </summary>
public class MaterialNameTests
{
    private readonly ITestOutputHelper _o;
    public MaterialNameTests(ITestOutputHelper o) => _o = o;

    /// <summary>The name tests as they were, word for word, before they read the table.</summary>
    private static string OldGroundName(string material) => material switch
    {
        "Concrete" => "sidewalk", "Asphalt" => "road", "Grass" => "grass", "Dirt" => "dirt", "Gravel" => "gravel",
        "Sand" => "sand", "Wood" => "boardwalk", "Metal" => "metal grating", "Water" => "water", _ => "outside",
    };
    private static bool OldRoad(string m) => string.Equals(m, "Asphalt", StringComparison.OrdinalIgnoreCase);
    private static bool OldCanopy(string m) => string.Equals(m, "Foliage", StringComparison.OrdinalIgnoreCase);
    private static bool OldGlazing(string m) => m == "Glass";
    private static bool OldPale(string m) => string.Equals(AcousticRegistry.Canonical(m), "Fence", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every material in the table, with each of the five properties, answers as its name did: the place's name, a
    /// road, a canopy, a window, a pale. The rows that differ are the rule doing its work (laminated glass is glass) and
    /// the ground names given since (snow, ice, solid timber, clay, leaves).
    /// </summary>
    [Fact]
    public void EachMaterialAnswersAsItsNameDid()
    {
        AcousticRegistry.Initialize();
        var differ = new List<string>();
        foreach (var name in AcousticRegistry.KnownMaterials())
        {
            var p = AcousticRegistry.GetProperties(name);
            string ground = Client.Core.ClientAudioSystem.OutdoorNameFor(name);
            _o.WriteLine($"{name,-15} ground {(p.GroundName ?? "-"),-14} carriageway {p.Carriageway,-5} leaves {p.Leaves,-5} glass {AcousticRegistry.IsGlass(name),-5} pales {p.Pales}");
            if (ground != OldGroundName(name)) differ.Add($"{name}: said '{ground}', said '{OldGroundName(name)}' before");
            if (AcousticRegistry.IsCarriageway(name) != OldRoad(name)) differ.Add($"{name}: a road {AcousticRegistry.IsCarriageway(name)}, before {OldRoad(name)}");
            if (AcousticRegistry.IsLeaves(name) != OldCanopy(name)) differ.Add($"{name}: a canopy {AcousticRegistry.IsLeaves(name)}, before {OldCanopy(name)}");
            if (AcousticRegistry.IsGlass(name) != OldGlazing(name)) differ.Add($"{name}: glazing {AcousticRegistry.IsGlass(name)}, before {OldGlazing(name)}");
            if ((p.Pales != null) != OldPale(name)) differ.Add($"{name}: pales {p.Pales}, before {OldPale(name)}");
        }
        // The rest are the names the table gave since (Cody, 2026-10-16): snow, ice, solid timber, clay and leaves were "outside".
        Assert.Equal(new[]
        {
            "Clay: said 'clay', said 'outside' before", "Foliage: said 'leaves', said 'outside' before",
            "Ice: said 'ice', said 'outside' before", "LaminatedGlass: glazing True, before False",
            "Maple: said 'boardwalk', said 'outside' before", "Oak: said 'boardwalk', said 'outside' before",
            "Pine: said 'boardwalk', said 'outside' before", "Snow: said 'snow', said 'outside' before",
        }, differ.OrderBy(d => d, StringComparer.Ordinal));
    }

    /// <summary>A fence struck is the pale it always was: 65 mm by 3 mm, a metre between its rails.</summary>
    [Fact]
    public void AFencesPaleIsAsItWas()
    {
        var t = StruckThings.Describe("Fence", new System.Numerics.Vector3(0.05f, 1.8f, 2.5f), System.Numerics.Vector3.UnitX, 0f, 0f,
                                      moves: false, isVehicle: false, out bool lengthIsUp);
        Assert.Equal((StruckShape.Bar, 1f, 0.065f, 0.003f, 0.3f, "Metal", true), (t.Shape, t.Length, t.Width, t.Thickness, t.PlayMm, t.LooseMaterial, lengthIsUp));
    }

    /// <summary>An alias is its material, as it never was to a name test: "Steel" underfoot is metal grating.</summary>
    [Fact]
    public void AnAliasHasItsMaterialsProperties()
    {
        Assert.Equal("metal grating", Client.Core.ClientAudioSystem.OutdoorNameFor("Steel"));
        Assert.Equal("dirt", Client.Core.ClientAudioSystem.OutdoorNameFor("Soil"));
        Assert.True(AcousticRegistry.IsGlass("SodaLimeGlass"));
        Assert.Equal("outside", Client.Core.ClientAudioSystem.OutdoorNameFor(""));
        Assert.Equal("outside", Client.Core.ClientAudioSystem.OutdoorNameFor("None"));
    }

    /// <summary>
    /// Where a name is still tested, and why it may be. "None" and "Generic" are the table's own words for no material
    /// and an unknown one. The rest choose a thing by its material rather than ask what the material is: a footstep
    /// recording (SoundMappingService.RecordedStandIn's wet sets, which 3.1 says stays), the concrete a path is laid in,
    /// the menu a sofa is listed under.
    /// </summary>
    private static readonly Dictionary<(string File, string Name), int> Allowed = new()
    {
        [("OpenFPS.Client.Core/Services/SoundMappingService.cs", "Concrete")] = 1,
        [("OpenFPS.Client.Core/Services/SoundMappingService.cs", "Dirt")] = 1,
        [("OpenFPS.Server/Editor/WorldEditor.Routes.cs", "Concrete")] = 2,
        [("OpenFPS.Server/Editor/WorldEditor.Menus.cs", "Audience")] = 1,
    };

    private static readonly HashSet<string> Sentinels = new(StringComparer.Ordinal) { "None", "Generic" };

    /// <summary>
    /// No source of the packages and the game compares anything with a material's name (or an alias) outside the
    /// material table, beyond <see cref="Allowed"/>: an ==, an Equals, a switch arm, a case or an "is". Choosing a
    /// material by name (a default, a generator's pick) is not a test and is not counted.
    /// </summary>
    [Fact]
    public void NoMaterialIsToldByItsName()
    {
        AcousticRegistry.Initialize();
        var names = new HashSet<string>(AcousticRegistry.KnownMaterials(), StringComparer.Ordinal)
            { "Steel", "MildSteel", "Stainless", "Aluminum", "SodaLimeGlass", "Soil", "Porcelain", "CeramicTile", "Plasterboard", "Gypsum" };
        foreach (var alias in names.ToList()) Assert.True(AcousticRegistry.IsKnown(alias), alias);

        var found = new Dictionary<(string File, string Name), int>();
        var where = new List<string>();
        var projects = EnginePackagesTests.SortedProjects.Where(p => p != "OpenFPS.Materials");
        string root = RepoRoot();
        foreach (var rel in LibraryBoundaryTests.SourceFiles(projects))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, rel)), new CSharpParseOptions(LanguageVersion.Preview));
            foreach (var lit in NameTests(tree.GetRoot()))
            {
                string value = lit.Token.ValueText;
                if (!names.Contains(value) || Sentinels.Contains(value)) continue;
                var key = (rel, AcousticRegistry.Canonical(value));
                found[key] = found.GetValueOrDefault(key) + 1;
                where.Add($"{rel}:{lit.GetLocation().GetLineSpan().StartLinePosition.Line + 1} \"{value}\"");
            }
        }
        foreach (var w in where) _o.WriteLine(w);
        var over = found.Where(kv => kv.Value > Allowed.GetValueOrDefault(kv.Key))
            .Select(kv => $"{kv.Key.File} tests the name \"{kv.Key.Name}\" {kv.Value} times (allowed {Allowed.GetValueOrDefault(kv.Key)})").ToList();
        Assert.True(over.Count == 0, "A material told by its name; read a property of the table instead:\n" + string.Join("\n", over)
                                     + "\n" + string.Join("\n", where));
        // The allowance only goes down: a name test that went is taken off it.
        var stale = Allowed.Where(kv => found.GetValueOrDefault(kv.Key) < kv.Value).Select(kv => $"{kv.Key}: {found.GetValueOrDefault(kv.Key)} of {kv.Value}").ToList();
        Assert.True(stale.Count == 0, "Lower the allowance:\n" + string.Join("\n", stale));
    }

    /// <summary>The repository root from the test's own folder: the sources are read where they are checked out.</summary>
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    /// <summary>The string literals a piece of code compares something with.</summary>
    internal static IEnumerable<LiteralExpressionSyntax> NameTests(SyntaxNode root)
    {
        foreach (var lit in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!lit.IsKind(SyntaxKind.StringLiteralExpression)) continue;
            var parent = lit.Parent;
            bool test = parent switch
            {
                BinaryExpressionSyntax b => b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression),
                ConstantPatternSyntax => true,
                CaseSwitchLabelSyntax => true,
                ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax inv } } => IsEquals(inv),
                _ => false,
            };
            if (test) yield return lit;
        }
    }

    private static bool IsEquals(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText is "Equals" or "StartsWith" or "EndsWith",
        IdentifierNameSyntax i => i.Identifier.ValueText == "Equals",
        _ => false,
    };

    /// <summary>The guard sees each kind of test, and not a pick.</summary>
    [Fact]
    public void TheGuardSeesEachKindOfTest()
    {
        var tree = CSharpSyntaxTree.ParseText("""
            class C {
                bool A(string m) => m == "Glass";
                bool B(string m) => string.Equals(m, "Asphalt", System.StringComparison.OrdinalIgnoreCase);
                string C2(string m) => m switch { "Concrete" => "sidewalk", _ => "" };
                bool D(string m) => m is "Foliage" or "Grass";
                void E(string m) { switch (m) { case "Water": break; } }
                bool F(string m) => m.Equals("Metal");
                string Pick() => "Brick";
                void G() { Use("Wood"); }
                void Use(string s) { }
            }
            """);
        var seen = NameTests(tree.GetRoot()).Select(l => l.Token.ValueText).ToList();
        Assert.Equal(new[] { "Glass", "Asphalt", "Concrete", "Foliage", "Grass", "Water", "Metal" }, seen);
    }
}

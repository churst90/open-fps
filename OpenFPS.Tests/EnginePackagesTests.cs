using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenFPS.Tests;

/// <summary>
/// The ratchet on the engine's packages (docs/ENGINE_PACKAGES.md, stage 0): every source file of the sorted
/// projects is in a package (EnginePackages/files.tsv), and no file references a package its own may not
/// (layers.tsv) more often than allowed.tsv says. The four lists are the survey's
/// (tools/engine_packages/run.sh writes them into its ratchet folder). The allowance only goes down: lower it
/// with OPENFPS_BOUNDARY_WRITE=1 in the commit that removes a crossing; a file that moves to another package
/// changes files.tsv and the survey's FILE_RULES together. Counted as the survey counts
/// (tools/sound_boundary/Program.cs, HandleName and AddEdge; tools/engine_packages/classify.py), over one
/// compilation of the same sources.
/// </summary>
public class EnginePackagesTests
{
    /// <summary>The projects the survey sorts (classify.py, SORTED_PROJECTS): the lab, the tests and the two
    /// heads use the packages and are in none.</summary>
    internal static readonly string[] SortedProjects =
    {
        "OpenFPS.Words", "OpenFPS.Schema", "OpenFPS.Native", "OpenFPS.Geometry", "OpenFPS.Astronomy", "OpenFPS.Physics",
        "OpenFPS.Acoustics", "OpenFPS.Sound", "OpenFPS.Common", "OpenFPS.Client.Core", "OpenFPS.Server",
    };

    /// <summary>open-fps itself: the server, the client's session and heads. It may reference anything.</summary>
    private const string Game = "game";

    /// <summary>What the survey counted with these lists when the ratchet was made (2026-10-12).</summary>
    private const int CrossingsWhenMade = 1301;

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    private static string ListPath(string name) => Path.Combine(RepoRoot(), "OpenFPS.Tests", "EnginePackages", name);

    private static IEnumerable<string[]> Rows(string name)
        => File.ReadAllLines(ListPath(name)).Skip(1).Where(l => l.Length > 0).Select(l => l.Split('\t'));

    /// <summary>Each sorted file's package.</summary>
    internal static Dictionary<string, string> ReadFiles() => Rows("files.tsv").ToDictionary(c => c[0], c => c[1], StringComparer.Ordinal);

    /// <summary>The types that go to a package of their own, whatever file they are in.</summary>
    internal static Dictionary<string, string> ReadTypes() => Rows("types.tsv").ToDictionary(c => c[0], c => c[1], StringComparer.Ordinal);

    /// <summary>What each package may reference besides itself and the runtime.</summary>
    internal static Dictionary<string, HashSet<string>> ReadLayers()
        => Rows("layers.tsv").ToDictionary(c => c[0],
            c => new HashSet<string>(c.Length > 2 && c[2].Length > 0 ? c[2].Split(',') : Array.Empty<string>(), StringComparer.Ordinal),
            StringComparer.Ordinal);

    private static Dictionary<(string File, string To), int> ReadAllowed()
        => Rows("allowed.tsv").ToDictionary(c => (c[0], c[1]), c => int.Parse(c[2]));

    private static void WriteAllowed(Dictionary<(string File, string To), int> counts)
    {
        var sb = new StringBuilder("file\tto_package\tcount\n");
        foreach (var (key, n) in counts.Where(kv => kv.Value > 0)
                     .OrderBy(kv => kv.Key.File, StringComparer.Ordinal).ThenBy(kv => kv.Key.To, StringComparer.Ordinal))
            sb.Append(key.File).Append('\t').Append(key.To).Append('\t').Append(n).Append('\n');
        File.WriteAllText(ListPath("allowed.tsv"), sb.ToString());
    }

    private static Lazy<CSharpCompilation> Compilation { get; } = new(() => LibraryBoundaryTests.Compile(SortedProjects, "EnginePackages"));

    private static readonly SymbolDisplayFormat Plain = SymbolDisplayFormat.CSharpErrorMessageFormat
        .WithGenericsOptions(SymbolDisplayGenericsOptions.None);

    /// <summary>Every source file of the sorted projects is in a package, and every listed file is there: a
    /// new file cannot slip past, and a moved or deleted one is followed in the list.</summary>
    [Fact]
    public void EveryFileIsInAPackage()
    {
        var listed = ReadFiles();
        var layers = ReadLayers();
        var onDisk = LibraryBoundaryTests.SourceFiles(SortedProjects).ToHashSet(StringComparer.Ordinal);
        var unlisted = onDisk.Where(f => !listed.ContainsKey(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var gone = listed.Keys.Where(f => !onDisk.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var unknown = listed.Where(kv => kv.Value != Game && !layers.ContainsKey(kv.Value)).Select(kv => $"{kv.Key}: {kv.Value}").ToList();
        var problems = new StringBuilder();
        if (unlisted.Count > 0)
            problems.AppendLine("Not in a package. Give each a rule in tools/engine_packages/classify.py (FILE_RULES) and a line in "
                + "OpenFPS.Tests/EnginePackages/files.tsv:\n  " + string.Join("\n  ", unlisted));
        if (gone.Count > 0)
            problems.AppendLine("Listed in OpenFPS.Tests/EnginePackages/files.tsv and not there (moved or deleted: follow it):\n  "
                + string.Join("\n  ", gone));
        if (unknown.Count > 0)
            problems.AppendLine("In a package layers.tsv does not know:\n  " + string.Join("\n  ", unknown));
        Assert.True(problems.Length == 0, problems.ToString());
        Assert.True(listed.Count > 500, $"only {listed.Count} files listed");
    }

    /// <summary>Every type sorted by its own rule is still there by that name, and goes to a known package.</summary>
    [Fact]
    public void EveryTypeRuleNamesAType()
    {
        var types = ReadTypes();
        var layers = ReadLayers();
        var found = TopLevelTypes(Compilation.Value).Select(t => t.ToDisplayString(Plain)).ToHashSet(StringComparer.Ordinal);
        var missing = types.Keys.Where(t => !found.Contains(t)).ToList();
        Assert.True(missing.Count == 0, "Not found (renamed or moved: follow it in EnginePackages/types.tsv and in "
            + "tools/engine_packages/classify.py, TYPE_RULES):\n  " + string.Join("\n  ", missing));
        Assert.All(types.Values, p => Assert.True(p == Game || layers.ContainsKey(p), $"unknown package {p}"));
    }

    /// <summary>The layers are an order: no package may reference one that may, by any chain, reference it; and
    /// each names only packages the table knows.</summary>
    [Fact]
    public void TheLayersHaveNoCycle()
    {
        var layers = ReadLayers();
        Assert.All(layers, kv => Assert.All(kv.Value, q => Assert.True(layers.ContainsKey(q), $"{kv.Key} may reference {q}, which is no package")));
        var level = new Dictionary<string, int>(StringComparer.Ordinal);
        var busy = new HashSet<string>(StringComparer.Ordinal);
        int Visit(string p)
        {
            if (level.TryGetValue(p, out int known)) return known;
            Assert.True(busy.Add(p), $"a cycle in layers.tsv through {p}");
            int l = layers[p].Count == 0 ? 0 : 1 + layers[p].Max(Visit);
            busy.Remove(p);
            return level[p] = l;
        }
        foreach (var p in layers.Keys) Visit(p);
        // The written layer is the worked-out one.
        foreach (var c in Rows("layers.tsv")) Assert.Equal(level[c[0]], int.Parse(c[1]));
        Assert.Equal(0, level["Geometry"]);
        Assert.True(level["Physics"] < level["Sound"] && level["Sound"] < level["Models"] && level["World"] < level["Common"]);
    }

    [Fact]
    public void NoNewCrossings()
    {
        var counts = Count(Compilation.Value, ReadFiles(), ReadTypes(), ReadLayers());
        var allowed = ReadAllowed();
        var over = new List<string>();
        var under = new List<string>();
        foreach (var (key, n) in counts.OrderBy(k => k.Key.File, StringComparer.Ordinal).ThenBy(k => k.Key.To, StringComparer.Ordinal))
        {
            allowed.TryGetValue(key, out int may);
            if (n > may) over.Add($"{key.File} -> {key.To}: {n} (allowed {may})");
            else if (n < may) under.Add($"{key.File} -> {key.To}: {n} (allowed {may})");
        }
        foreach (var (key, may) in allowed)
            if (!counts.ContainsKey(key)) under.Add($"{key.File} -> {key.To}: 0 (allowed {may})");

        string? write = Environment.GetEnvironmentVariable("OPENFPS_BOUNDARY_WRITE");
        if (write == "1" && under.Count > 0)
        {
            WriteAllowed(allowed.ToDictionary(kv => kv.Key, kv => Math.Min(counts.GetValueOrDefault(kv.Key), kv.Value)));
            under.Clear();
        }
        else if (write == "all")
        {
            WriteAllowed(counts);
            over.Clear();
            under.Clear();
        }

        var problems = new StringBuilder();
        if (over.Count > 0)
            problems.AppendLine("A file references a package its own may not (docs/ENGINE_PACKAGES.md, the layers; "
                + "OpenFPS.Tests/EnginePackages/layers.tsv). Take what it needs as a value or an interface of a package it may "
                + "reference, or move the file:\n  " + string.Join("\n  ", over));
        if (under.Count > 0)
            problems.AppendLine("Fewer crossings than allowed: good. Lower the allowance in the same commit (run this test with "
                + "OPENFPS_BOUNDARY_WRITE=1):\n  " + string.Join("\n  ", under));
        Assert.True(problems.Length == 0, problems.ToString());
    }

    /// <summary>The allowance has not been raised past what the survey counted when the ratchet was made.</summary>
    [Fact]
    public void TheAllowanceIsNoMoreThanTheSurveyFound()
    {
        int sum = ReadAllowed().Values.Sum();
        Assert.True(sum <= CrossingsWhenMade, $"allowed.tsv holds {sum} crossings, the survey {CrossingsWhenMade}");
    }

    /// <summary>
    /// Each project that is a package already, by an assembly of it, references only the projects of packages
    /// its layer allows (and the runtime and its decided libraries, which LibraryBoundaryTests holds). A package
    /// that becomes a project joins this list in the commit that makes it.
    /// </summary>
    public static IEnumerable<object[]> PackageAssemblies() => new[]
    {
        new object[] { "Words", typeof(OpenFPS.Words.CompassWords).Assembly.GetName().Name! },
        new object[] { "Schema", typeof(OpenFPS.Common.Editing.TunableAttribute).Assembly.GetName().Name! },
        new object[] { "Geometry", typeof(OpenFPS.Common.Geometry.TriangleWorld).Assembly.GetName().Name! },
        new object[] { "Native", typeof(FMOD.System).Assembly.GetName().Name! },
        new object[] { "Astronomy", typeof(OpenFPS.Astronomy.Moon).Assembly.GetName().Name! },
        new object[] { "Physics", typeof(OpenFPS.Common.SharedMovementEngine).Assembly.GetName().Name! },
        new object[] { "Acoustics", typeof(OpenFPS.Common.AcousticRegistry).Assembly.GetName().Name! },
        new object[] { "Sound", typeof(OpenFPS.Common.Loudness).Assembly.GetName().Name! },
    };

    [Theory]
    [MemberData(nameof(PackageAssemblies))]
    public void EachPackageReferencesItsLayer(string package, string assembly)
    {
        Assert.Equal("OpenFPS." + package, assembly);
        var may = ReadLayers()[package];
        var wrong = System.Reflection.Assembly.Load(assembly).GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => n.StartsWith("OpenFPS.", StringComparison.Ordinal) && !may.Contains(n.Substring("OpenFPS.".Length)))
            .ToList();
        Assert.True(wrong.Count == 0, $"{assembly} references {string.Join(", ", wrong)}: its layer allows "
            + (may.Count == 0 ? "no package" : string.Join(", ", may.OrderBy(p => p, StringComparer.Ordinal))) + ".");
    }

    /// <summary>The count is of references, by the survey's rules: a reference from a file of one package to a
    /// type of a package it may not reference counts, once for each name; one to its own package, to a package
    /// it may reference, from the game, or written in a comment or a string does not; and a type sorted by its
    /// own rule goes by that rule, both as what refers and as what is referred to.</summary>
    [Fact]
    public void TheCountIsOfReferencesByTheSurveysRules()
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        SyntaxTree Tree(string path, string text) => CSharpSyntaxTree.ParseText(text, parse, path, Encoding.UTF8);
        var trees = new[]
        {
            Tree("low/Low.cs", "namespace N; public static class Low { public static int One = 1; public static int Two() => 2; }"),
            Tree("high/High.cs", "namespace N; public static class High { public static int Three = 3; public class Inner { } }"),
            Tree("high/Ruled.cs", "namespace N; public static class Ruled { public static int Four = 4; }"),
            // Low may not reference High. Every name counts, the type's and its member's: High.Three is two,
            // written twice, and High.Inner two more.
            Tree("low/Uses.cs", "namespace N; public static class Uses { /* High.Three */ public const string S = \"High.Three\";\n"
                + "public static int A = High.Three + High.Three; public static High.Inner? B; public static int C = Low.One + Low.Two(); }"),
            // High may reference Low: nothing counts.
            Tree("high/Down.cs", "namespace N; public static class Down { public static int A = Low.One + Low.Two(); }"),
            // The game may reference anything.
            Tree("game/Any.cs", "namespace N; public static class Any { public static int A = High.Three + Low.One + Ruled.Four; }"),
            // Ruled is sorted to Low by its own rule though its file is High's: Low's file may name it, and what
            // it names in High counts against Low from its own file.
            Tree("low/UsesRuled.cs", "namespace N; public static class UsesRuled { public static int A = Ruled.Four; }"),
            Tree("high/RuledMore.cs", "namespace N; public static partial class RuledToo { public static int A = High.Three; }"),
        };
        var compilation = CSharpCompilation.Create("Rules", trees,
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var files = trees.ToDictionary(t => t.FilePath, t => t.FilePath.Split('/')[0] switch { "low" => "Low", "high" => "High", _ => Game });
        var types = new Dictionary<string, string> { ["N.Ruled"] = "Low", ["N.RuledToo"] = "Low" };
        var layers = new Dictionary<string, HashSet<string>> { ["Low"] = new(), ["High"] = new() { "Low" } };
        var counts = Count(compilation, files, types, layers);
        Assert.Equal(new Dictionary<(string, string), int>
        {
            [("low/Uses.cs", "High")] = 6,
            [("high/RuledMore.cs", "High")] = 2,
        }, counts);
    }

    private static IEnumerable<INamedTypeSymbol> TopLevelTypes(Compilation compilation)
    {
        var stack = new Stack<INamespaceSymbol>();
        stack.Push(compilation.Assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            var ns = stack.Pop();
            foreach (var m in ns.GetMembers())
                if (m is INamespaceSymbol inner) stack.Push(inner);
                else if (m is INamedTypeSymbol t) yield return t;
        }
    }

    /// <summary>
    /// Every reference from a file to a type of a package the file's own may not reference, by file and the
    /// package referred to. The package a reference is made from is its enclosing top-level type's own rule,
    /// else its file's; the package referred to is the type's own rule, else that of the first file that
    /// declares it. Every simple name that binds to a type or to a member of one, and every target-typed
    /// <c>new()</c>, keyed by the outermost type; a type's references to itself do not count.
    /// </summary>
    internal static Dictionary<(string File, string To), int> Count(Compilation compilation, Dictionary<string, string> files,
                                                                    Dictionary<string, string> types, Dictionary<string, HashSet<string>> layers)
    {
        var counts = new Dictionary<(string, string), int>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!files.TryGetValue(tree.FilePath, out string? filePackage)) continue;
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                INamedTypeSymbol? target = null;
                if (node is SimpleNameSyntax name)
                {
                    var info = model.GetSymbolInfo(name);
                    var sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                    if (sym is IAliasSymbol alias) sym = alias.Target;
                    target = sym switch
                    {
                        INamedTypeSymbol nt => nt,
                        IMethodSymbol ms => (ms.ReducedFrom ?? ms).ContainingType,
                        IFieldSymbol fs => fs.ContainingType,
                        IPropertySymbol ps => ps.ContainingType,
                        IEventSymbol es => es.ContainingType,
                        _ => null,
                    };
                }
                else if (node is ImplicitObjectCreationExpressionSyntax ioc && model.GetSymbolInfo(ioc).Symbol is IMethodSymbol ctor)
                    target = ctor.ContainingType;
                if (target == null) continue;
                var outer = (INamedTypeSymbol)target.OriginalDefinition;
                while (outer.ContainingType != null) outer = outer.ContainingType;
                if (outer.DeclaringSyntaxReferences.Length == 0) continue;
                string to = outer.ToDisplayString(Plain);

                // The package referred to: the type's own rule, else its first file's.
                if (!types.TryGetValue(to, out string? toPackage)
                    && !files.TryGetValue(outer.DeclaringSyntaxReferences[0].SyntaxTree.FilePath, out toPackage)) continue;

                // The package it is made from: the enclosing top-level type's own rule, else this file's.
                string fromPackage = filePackage;
                var decl = node.AncestorsAndSelf().LastOrDefault(a => a is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
                if (decl != null && model.GetDeclaredSymbol(decl) is INamedTypeSymbol enclosing)
                {
                    string from = enclosing.ToDisplayString(Plain);
                    if (from == to) continue;
                    if (types.TryGetValue(from, out string? ruled)) fromPackage = ruled;
                }

                if (fromPackage == toPackage || fromPackage == Game) continue;
                if (layers.TryGetValue(fromPackage, out var may) && may.Contains(toPackage)) continue;
                var key = (tree.FilePath, toPackage);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }
        return counts;
    }
}

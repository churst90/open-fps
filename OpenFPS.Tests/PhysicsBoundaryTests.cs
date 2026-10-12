using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenFPS.Tests;

/// <summary>
/// The ratchet on the physics boundary (docs/PHYSICS_BOUNDARY.md, stage 0 and section 9): no type bound for
/// OpenFPS.Physics whole (P) or as a shared value (V) references anything but Physics' own types and
/// Geometry more often than allowed.tsv says. The types and their groups are the survey's
/// (tools/physics_boundary/classify.py, RULES), in types.tsv. The allowance only goes down: lower it with
/// OPENFPS_BOUNDARY_WRITE=1 in the commit that fixes a crossing. Counted as the survey and
/// LibraryBoundaryTests count, over the same compilation.
/// </summary>
public class PhysicsBoundaryTests
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    private static string ListPath(string name) => Path.Combine(RepoRoot(), "OpenFPS.Tests", "PhysicsBoundary", name);

    private static readonly SymbolDisplayFormat Plain = SymbolDisplayFormat.CSharpErrorMessageFormat
        .WithGenericsOptions(SymbolDisplayGenericsOptions.None);

    private static Dictionary<string, string> ReadTypes()
        => File.ReadAllLines(ListPath("types.tsv")).Skip(1).Where(l => l.Length > 0)
               .Select(l => l.Split('\t')).ToDictionary(c => c[0], c => c[1], StringComparer.Ordinal);

    private static Dictionary<(string From, string To), int> ReadAllowed()
    {
        var d = new Dictionary<(string, string), int>();
        foreach (var line in File.ReadAllLines(ListPath("allowed.tsv")).Skip(1))
        {
            if (line.Length == 0) continue;
            var c = line.Split('\t');
            d[(c[0], c[1])] = int.Parse(c[2]);
        }
        return d;
    }

    private static Lazy<CSharpCompilation> Compilation { get; } = new(LibraryBoundaryTests.CompileSorted);

    /// <summary>Every type the survey sorted into Physics or Geometry is still there by that name: a renamed or
    /// moved type would otherwise drop out of the count unseen.</summary>
    [Fact]
    public void EverySortedTypeIsFound()
    {
        var types = ReadTypes();
        var found = AllTypes(Compilation.Value).Select(t => t.ToDisplayString(Plain)).ToHashSet(StringComparer.Ordinal);
        var missing = types.Keys.Where(t => !found.Contains(t)).ToList();
        Assert.True(missing.Count == 0, "Not found (renamed or moved: follow it in types.tsv and in tools/physics_boundary/classify.py):\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// The physics project references the runtime, the geometry and the editor's attribute, and nothing else
    /// (section 9; decision 7 for the attribute). The compiler refuses a type from a project that is not
    /// referenced; this refuses the reference itself, which is what someone would add to make that error go
    /// away.
    /// </summary>
    [Fact]
    public void PhysicsReferencesOnlyGeometry()
    {
        var allowed = new[] { "OpenFPS.Geometry", "OpenFPS.Schema" };
        var wrong = typeof(OpenFPS.Common.SharedMovementEngine).Assembly.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => !(n == "System" || n.StartsWith("System.", StringComparison.Ordinal) || n is "netstandard" or "mscorlib"
                          || n.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) || allowed.Contains(n)))
            .ToList();
        Assert.Equal("OpenFPS.Physics", typeof(OpenFPS.Common.SharedMovementEngine).Assembly.GetName().Name);
        Assert.True(wrong.Count == 0, $"OpenFPS.Physics references {string.Join(", ", wrong)}: it may reference the runtime, "
            + "OpenFPS.Geometry and OpenFPS.Schema only.");
    }

    /// <summary>What steps the world. The sound may read the physics' values and presets; it never steps it
    /// (section 9). A stepper added later (a physics world, a vehicle step) goes on this list.</summary>
    private static readonly string[] Steppers = { "OpenFPS.Common.SharedMovementEngine" };

    /// <summary>
    /// Nothing in the sound project names a stepper: the server steps the physics, the client predicts the
    /// player's own body with it, and the sound takes its inputs from the state they produce.
    /// </summary>
    [Fact]
    public void TheSoundNeverStepsThePhysics()
    {
        var compilation = Compilation.Value;
        var found = new List<string>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!tree.FilePath.StartsWith("OpenFPS.Sound/", StringComparison.Ordinal)) continue;
            var model = compilation.GetSemanticModel(tree);
            foreach (var name in tree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
            {
                var info = model.GetSymbolInfo(name);
                var sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
                var type = sym as INamedTypeSymbol ?? sym?.ContainingType;
                while (type?.ContainingType != null) type = type.ContainingType;
                if (type != null && Steppers.Contains(type.ToDisplayString(Plain)))
                    found.Add($"{tree.FilePath}:{name.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {name.Parent}");
            }
        }
        Assert.True(found.Count == 0, "The sound steps the physics (docs/PHYSICS_BOUNDARY.md, section 9):\n  " + string.Join("\n  ", found));
    }

    [Fact]
    public void NoNewCrossings()
    {
        var types = ReadTypes();
        var compilation = Compilation.Value;
        var counts = Count(compilation, types);

        var allowed = ReadAllowed();
        var over = new List<string>();
        var under = new List<string>();
        foreach (var (key, n) in counts.OrderBy(k => k.Key.From, StringComparer.Ordinal).ThenBy(k => k.Key.To, StringComparer.Ordinal))
        {
            allowed.TryGetValue(key, out int may);
            if (n > may) over.Add($"{key.From} -> {key.To}: {n} (allowed {may})");
            else if (n < may) under.Add($"{key.From} -> {key.To}: {n} (allowed {may})");
        }
        foreach (var (key, may) in allowed)
            if (!counts.ContainsKey(key)) under.Add($"{key.From} -> {key.To}: 0 (allowed {may})");

        string? write = Environment.GetEnvironmentVariable("OPENFPS_BOUNDARY_WRITE");
        if (write == "1" && under.Count > 0)
        {
            var lowered = allowed.ToDictionary(kv => kv.Key, kv => Math.Min(counts.GetValueOrDefault(kv.Key), kv.Value));
            WriteAllowed(lowered);
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
            problems.AppendLine("A type bound for OpenFPS.Physics gained a reference outside Physics and Geometry (docs/PHYSICS_BOUNDARY.md, "
                + "section 9). Take it as a value Physics owns instead:\n  " + string.Join("\n  ", over));
        if (under.Count > 0)
            problems.AppendLine("Fewer crossings than allowed: good. Lower the allowance in the same commit (run this test with "
                + "OPENFPS_BOUNDARY_WRITE=1):\n  " + string.Join("\n  ", under));
        Assert.True(problems.Length == 0, problems.ToString());
    }

    /// <summary>The survey counted 62 at 0228374a, in five files (physics_crossings.tsv beside the tool); the
    /// allowance must not have been raised past it.</summary>
    [Fact]
    public void TheAllowanceIsNoMoreThanTheSurveyFound()
    {
        Assert.True(ReadAllowed().Values.Sum() <= 62, $"allowed.tsv holds {ReadAllowed().Values.Sum()} crossings, the survey 62");
    }

    private static void WriteAllowed(Dictionary<(string From, string To), int> counts)
    {
        var sb = new StringBuilder("from_type\tto_type\tcount\n");
        foreach (var (key, n) in counts.Where(kv => kv.Value > 0)
                     .OrderBy(kv => kv.Key.From, StringComparer.Ordinal).ThenBy(kv => kv.Key.To, StringComparer.Ordinal))
            sb.Append(key.From).Append('\t').Append(key.To).Append('\t').Append(n).Append('\n');
        File.WriteAllText(ListPath("allowed.tsv"), sb.ToString());
    }

    private static IEnumerable<INamedTypeSymbol> AllTypes(Compilation compilation)
    {
        var stack = new Stack<INamespaceOrTypeSymbol>();
        stack.Push(compilation.Assembly.GlobalNamespace);
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            foreach (var m in s.GetMembers())
                if (m is INamespaceSymbol ns) stack.Push(ns);
                else if (m is INamedTypeSymbol t) { yield return t; stack.Push(t); }
        }
    }

    /// <summary>
    /// Every reference from the declarations of a P or V type to a repository type that is neither in the
    /// inventory's Physics and Geometry groups nor in OpenFPS.Geometry or OpenFPS.Schema, keyed by the two types (outermost).
    /// </summary>
    private static Dictionary<(string From, string To), int> Count(CSharpCompilation compilation, Dictionary<string, string> types)
    {
        bool Allowed(INamedTypeSymbol outer)
        {
            if (types.ContainsKey(outer.ToDisplayString(Plain))) return true;
            // OpenFPS.Schema is the editor's attribute alone, under every part (decision 7).
            return outer.DeclaringSyntaxReferences.All(r => r.SyntaxTree.FilePath.StartsWith("OpenFPS.Geometry/", StringComparison.Ordinal)
                                                       || r.SyntaxTree.FilePath.StartsWith("OpenFPS.Schema/", StringComparison.Ordinal)
                                                       || r.SyntaxTree.FilePath.StartsWith("OpenFPS.Physics/", StringComparison.Ordinal));
        }
        var counts = new Dictionary<(string, string), int>();
        foreach (var type in AllTypes(compilation))
        {
            string name = type.ToDisplayString(Plain);
            if (!types.TryGetValue(name, out var group) || group is not ("P" or "V")) continue;
            foreach (var decl in type.DeclaringSyntaxReferences)
            {
                var model = compilation.GetSemanticModel(decl.SyntaxTree);
                foreach (var node in decl.GetSyntax().DescendantNodesAndSelf())
                {
                    INamedTypeSymbol? target = null;
                    if (node is SimpleNameSyntax simple)
                    {
                        var info = model.GetSymbolInfo(simple);
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
                    if (outer.DeclaringSyntaxReferences.Length == 0 || Allowed(outer)) continue;
                    var key = (name, outer.ToDisplayString(Plain));
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }
        return counts;
    }
}

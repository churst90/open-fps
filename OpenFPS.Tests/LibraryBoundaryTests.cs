using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OpenFPS.Tests;

/// <summary>
/// The ratchet on the sound library's boundary (docs/SOUND_LIBRARY_BOUNDARY.md, sections 9 and 13.1):
/// no library file references a host type more often than allowed.tsv says, and every file of a sorted
/// project is sorted in files.tsv. Lowering the counts (OPENFPS_BOUNDARY_WRITE=1) and moving a file
/// (=all) are in section 13.4. Bound with Roslyn without source generators: a call into MemoryPack's
/// generated code does not bind, and no host type is reached that way.
/// </summary>
public class LibraryBoundaryTests
{
    /// <summary>The projects whose files are sorted: the two the library is leaving, and the library's own.</summary>
    internal static readonly string[] SortedProjects =
    {
        "OpenFPS.Common", "OpenFPS.Client.Core",
        "OpenFPS.Geometry", "OpenFPS.Acoustics", "OpenFPS.Sound", "OpenFPS.Native", "OpenFPS.Audio",
    };

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    private static string ListPath(string name) => Path.Combine(RepoRoot(), "OpenFPS.Tests", "LibraryBoundary", name);

    internal sealed record FileEntry(string Path, string Group, string Projects, string[] LibraryTypes);

    internal static List<FileEntry> ReadFiles()
    {
        var list = new List<FileEntry>();
        foreach (var line in File.ReadAllLines(ListPath("files.tsv")).Skip(1))
        {
            if (line.Length == 0) continue;
            var c = line.Split('\t');
            list.Add(new FileEntry(c[0], c[1], c.Length > 2 ? c[2] : "",
                c.Length > 3 && c[3].Length > 0 ? c[3].Split(',') : Array.Empty<string>()));
        }
        return list;
    }

    private static Dictionary<(string File, string Name), int> ReadAllowed()
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

    /// <summary>Every .cs file of the sorted projects, repository-relative with forward slashes.</summary>
    private static IEnumerable<string> SourceFiles()
    {
        string root = RepoRoot();
        foreach (var project in SortedProjects)
        {
            string dir = Path.Combine(root, project);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                string inProject = rel.Substring(project.Length + 1);
                if (inProject.StartsWith("obj/") || inProject.StartsWith("bin/")) continue;
                yield return rel;
            }
        }
    }

    [Fact]
    public void EveryFileIsSorted()
    {
        var listed = ReadFiles().Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var onDisk = SourceFiles().ToHashSet(StringComparer.Ordinal);
        var unsorted = onDisk.Where(f => !listed.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var gone = listed.Where(f => !onDisk.Contains(f)).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var problems = new StringBuilder();
        if (unsorted.Count > 0)
            problems.AppendLine("New files not sorted into library or host (add a rule to tools/sound_boundary/classify.py if "
                + "the default is wrong, run tools/sound_boundary/run.sh and copy its files.tsv into OpenFPS.Tests/LibraryBoundary):\n  "
                + string.Join("\n  ", unsorted));
        if (gone.Count > 0)
            problems.AppendLine("Listed in files.tsv but not on disk (moved or deleted: update the list in the same commit):\n  "
                + string.Join("\n  ", gone));
        Assert.True(problems.Length == 0, problems.ToString());
    }

    [Fact]
    public void NoNewCrossings()
    {
        var files = ReadFiles();
        var byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
        var compilation = CompileSorted();
        var isHost = IsHost(byPath);
        var counts = new Dictionary<(string File, string Name), int>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!byPath.TryGetValue(tree.FilePath, out var entry) || entry.Group == "host") continue;
            foreach (var (name, n) in Crossings(compilation, tree, entry.Group == "mixed" ? entry.LibraryTypes : null, isHost))
                counts[(entry.Path, name)] = n;
        }

        var allowed = ReadAllowed();
        var over = new List<string>();
        var under = new List<string>();
        foreach (var (key, n) in counts.OrderBy(k => k.Key.File, StringComparer.Ordinal).ThenBy(k => k.Key.Name, StringComparer.Ordinal))
        {
            allowed.TryGetValue(key, out int may);
            if (n > may) over.Add($"{key.File}: {key.Name} {n} (allowed {may})");
            else if (n < may) under.Add($"{key.File}: {key.Name} {n} (allowed {may})");
        }
        foreach (var (key, may) in allowed)
            if (!counts.ContainsKey(key)) under.Add($"{key.File}: {key.Name} 0 (allowed {may})");

        string? write = Environment.GetEnvironmentVariable("OPENFPS_BOUNDARY_WRITE");
        if (write == "1" && under.Count > 0)
        {
            // Lower only: a count over its allowance stays a failure below.
            var lowered = new Dictionary<(string File, string Name), int>();
            foreach (var (key, may) in allowed)
            {
                counts.TryGetValue(key, out int n);
                if (Math.Min(n, may) > 0) lowered[key] = Math.Min(n, may);
            }
            WriteAllowed(lowered);
            under.Clear();
        }
        else if (write == "all")
        {
            // The whole list as it is, raised counts included: only for a moved file or the first list.
            WriteAllowed(counts);
            over.Clear();
            under.Clear();
        }

        var problems = new StringBuilder();
        if (over.Count > 0)
            problems.AppendLine("Library code gained a reference to a host type (docs/SOUND_LIBRARY_BOUNDARY.md, section 9). "
                + "Take what it needs as a value or an interface the library owns instead:\n  " + string.Join("\n  ", over));
        if (under.Count > 0)
            problems.AppendLine("Fewer host references than allowed: good. Lower the allowance in the same commit "
                + "(run this test with OPENFPS_BOUNDARY_WRITE=1):\n  " + string.Join("\n  ", under));
        Assert.True(problems.Length == 0, problems.ToString());
    }

    private static void WriteAllowed(Dictionary<(string File, string Name), int> counts)
    {
        var sb = new StringBuilder("file\tname\tcount\n");
        foreach (var (key, n) in counts.Where(kv => kv.Value > 0)
                     .OrderBy(kv => kv.Key.File, StringComparer.Ordinal).ThenBy(kv => kv.Key.Name, StringComparer.Ordinal))
            sb.Append(key.File).Append('\t').Append(key.Name).Append('\t').Append(n).Append('\n');
        File.WriteAllText(ListPath("allowed.tsv"), sb.ToString());
    }

    /// <summary>All sorted projects' sources in one compilation, each tree named by its repository path.</summary>
    private static CSharpCompilation CompileSorted()
    {
        string root = RepoRoot();
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = new List<SyntaxTree>();
        foreach (var rel in SourceFiles().OrderBy(f => f, StringComparer.Ordinal))
            trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root, rel)), parse, rel, Encoding.UTF8));
        // What the build adds: the implicit usings, and the two classes Common's project file writes.
        trees.Add(CSharpSyntaxTree.ParseText(string.Join("\n", new[]
            {
                "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading",
                "System.Threading.Tasks",
            }.Select(n => $"global using global::{n};")), parse, "<generated>/GlobalUsings.g.cs"));
        trees.Add(CSharpSyntaxTree.ParseText(
            "namespace OpenFPS.Common; public static class WireContract { public const string Hash = \"test\"; }\n"
            + "public static class DoorModelFingerprint { public const string Hash = \"test\"; }", parse, "<generated>/Generated.g.cs"));
        return CSharpCompilation.Create("LibraryBoundary", trees, References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable));
    }

    /// <summary>The runtime and every package beside this test, but not open-fps's own assemblies: their
    /// sources are in the compilation.</summary>
    private static List<MetadataReference> References()
    {
        var refs = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        foreach (var dir in new[] { runtime, AppContext.BaseDirectory })
            foreach (var dll in Directory.GetFiles(dir, "*.dll"))
            {
                string name = Path.GetFileName(dll);
                if (name.StartsWith("OpenFPS.", StringComparison.Ordinal) || !seen.Add(name)) continue;
                try { System.Reflection.AssemblyName.GetAssemblyName(dll); }
                catch (BadImageFormatException) { continue; }   // a native library
                refs.Add(MetadataReference.CreateFromFile(dll));
            }
        return refs;
    }

    /// <summary>A host type: declared in a host file, or in a mixed file and not one of its library types.</summary>
    private static Func<INamedTypeSymbol, bool> IsHost(Dictionary<string, FileEntry> byPath) => type =>
    {
        foreach (var r in type.DeclaringSyntaxReferences)
        {
            if (!byPath.TryGetValue(r.SyntaxTree.FilePath, out var entry)) continue;
            if (entry.Group == "host") return true;
            if (entry.Group == "mixed" && !entry.LibraryTypes.Contains(type.Name)) return true;
        }
        return false;
    };

    /// <summary>
    /// The references to host types in one file: in all of it, or only in the declarations of
    /// <paramref name="libraryTypes"/> for a file that holds host types too. Counted as the survey
    /// counts them (tools/sound_boundary/Program.cs, HandleName): every simple name that binds to a type
    /// or to a member of one, and every target-typed <c>new()</c>, keyed by the outermost type.
    /// </summary>
    internal static Dictionary<string, int> Crossings(Compilation compilation, SyntaxTree tree, string[]? libraryTypes,
                                                      Func<INamedTypeSymbol, bool> isHost)
    {
        var model = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        IEnumerable<SyntaxNode> scopes = libraryTypes == null
            ? new[] { root }
            : root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
                  .Where(n => (n is BaseTypeDeclarationSyntax t && libraryTypes.Contains(t.Identifier.ValueText))
                           || (n is DelegateDeclarationSyntax d && libraryTypes.Contains(d.Identifier.ValueText)));
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var scope in scopes)
            foreach (var node in scope.DescendantNodesAndSelf())
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
                if (outer.DeclaringSyntaxReferences.Length == 0 || !isHost(outer)) continue;
                string key = outer.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat
                    .WithGenericsOptions(SymbolDisplayGenericsOptions.None));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        return counts;
    }

    /// <summary>
    /// Each library project, by an assembly of it, and what it may reference besides the runtime: the
    /// packages decided for it (docs/SOUND_LIBRARY_BOUNDARY.md, section 11) and lower library projects.
    /// The compiler refuses a type from an assembly that is not referenced; this refuses the reference
    /// itself, which is what someone would add to make that error go away.
    /// </summary>
    public static IEnumerable<object[]> LibraryAssemblies() => new[]
    {
        // The triangle world, shapes, tiles, the grid. MemoryPack: a collider's ShapeSpec is on the wire.
        new object[] { typeof(OpenFPS.Common.Geometry.TriangleWorld).Assembly.GetName().Name!, new[] { "MemoryPack.Core" } },
        // FMOD's wrapper and the Steam Audio bindings. Serilog: BackgroundPriority logs (decision 5).
        new object[] { typeof(FMOD.System).Assembly.GetName().Name!, new[] { "Serilog" } },
        // Materials, walls, diffraction, reflections, the octree. Serilog: an unknown material is logged.
        new object[] { typeof(OpenFPS.Common.AcousticRegistry).Assembly.GetName().Name!,
                       new[] { "OpenFPS.Geometry", "Serilog" } },
        // The sound models and the synthesis. MemoryPack: TransientSound is on the wire.
        new object[] { typeof(OpenFPS.Common.Loudness).Assembly.GetName().Name!,
                       new[] { "OpenFPS.Geometry", "OpenFPS.Acoustics", "MemoryPack.Core", "Serilog" } },
    };

    [Theory]
    [MemberData(nameof(LibraryAssemblies))]
    public void LibraryReferencesOnlyLibrary(string assembly, string[] allowed)
    {
        var asm = System.Reflection.Assembly.Load(assembly);
        var wrong = asm.GetReferencedAssemblies()
            .Select(r => r.Name!)
            .Where(n => !(n == "System" || n.StartsWith("System.", StringComparison.Ordinal) || n is "netstandard" or "mscorlib"
                          || n.StartsWith("Microsoft.Win32.", StringComparison.Ordinal) || allowed.Contains(n)))
            .ToList();
        Assert.True(wrong.Count == 0, $"{assembly} references {string.Join(", ", wrong)}: a library project references only "
            + "the runtime, its decided packages and lower library projects.");
    }

    [Fact]
    public void CrossingsCountReferencesNotCommentsOrStrings()
    {
        const string host = """
            namespace Game;
            public sealed class Snapshot { public int Count; public static Snapshot Empty = new(); }
            """;
        const string library = """
            namespace Lib;
            using Game;
            // A Snapshot in a comment
            public static class Uses
            {
                /// <summary>A <see cref="Snapshot"/> in a doc comment.</summary>
                public static int Count(Snapshot w)
                {
                    var copy = w;
                    return "Snapshot".Length + copy.Count + Snapshot.Empty.Count;
                }
            }
            public static class Clean { public static int Twice(int x) => 2 * x; }
            """;
        var hostTree = CSharpSyntaxTree.ParseText(host, path: "host.cs");
        var libTree = CSharpSyntaxTree.ParseText(library, path: "lib.cs");
        var compilation = CSharpCompilation.Create("t", new[] { hostTree, libTree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Func<INamedTypeSymbol, bool> isHost = t => t.DeclaringSyntaxReferences.Any(r => r.SyntaxTree.FilePath == "host.cs");

        // The parameter's type, `var`, copy.Count, Snapshot, .Empty and its .Count; not the comment,
        // the doc comment's cref or the string.
        var all = Crossings(compilation, libTree, null, isHost);
        Assert.Equal(6, all["Game.Snapshot"]);
        Assert.Empty(Crossings(compilation, libTree, new[] { "Clean" }, isHost));
    }
}

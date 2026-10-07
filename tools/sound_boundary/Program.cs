// Sound library boundary survey (docs/SOUND_LIBRARY_BOUNDARY.md).
//
// Reads every project's sources with Roslyn, binds them against the runtime and the cached NuGet
// packages, and writes the tables classify.py reads:
//   types.tsv        every top-level type declared in the repository
//   edges.tsv        every reference from one top-level type to another, with file:line
//   statics.tsv      every static field or property whose value or contents can change
//   static_refs.tsv  every read, write or mutating call on those statics
//   assets.tsv       every file-system, base-directory, environment or asset-path use
//   members.tsv      every method, property and field with its line span (for the per-method split)
//
// Usage: SoundBoundary <repo root> <output dir>. Nothing is built from the repository: a missing
// package only leaves some calls unbound, and a reference to a repository type always binds.

using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

string repo = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
string outDir = Path.GetFullPath(args.Length > 1 ? args[1] : "boundary-out");
Directory.CreateDirectory(outDir);

string nuget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
string runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

var baseRefs = new List<MetadataReference>();
foreach (var dll in Directory.GetFiles(runtimeDir, "*.dll"))
    if (IsManaged(dll)) baseRefs.Add(MetadataReference.CreateFromFile(dll));

string[] packages =
{
    "arch", "arch.lowlevel", "collections.pooled", "communitytoolkit.highperformance", "zeroallocjobscheduler",
    "litenetlib", "memorypack.core", "serilog", "serilog.sinks.console", "serilog.sinks.file", "concentus",
    "naudio", "naudio.core", "naudio.wasapi", "naudio.winmm", "bcrypt.net-next",
    "microsoft.extensions.dependencyinjection", "microsoft.extensions.dependencyinjection.abstractions",
    "microsoft.entityframeworkcore", "microsoft.entityframeworkcore.abstractions",
    "microsoft.entityframeworkcore.relational", "microsoft.entityframeworkcore.sqlite.core",
    "xunit.assert", "xunit.core", "xunit.abstractions", "xunit.extensibility.core", "xunit.extensibility.execution",
    "system.speech", "gircore.gtk-4.0", "gircore.gobject-2.0", "gircore.glib-2.0", "gircore.gio-2.0", "gircore.gdk-4.0",
};
foreach (var p in packages)
    foreach (var dll in PackageDlls(p))
        baseRefs.Add(MetadataReference.CreateFromFile(dll));

string implicitUsings = string.Join("\n", new[]
{
    "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading",
    "System.Threading.Tasks",
}.Select(n => $"global using global::{n};"));

var projects = new (string Name, string Dir, string[] Refs, string[] Extra)[]
{
    // The library's projects first, lowest first (docs/SOUND_LIBRARY_BOUNDARY.md, section 6).
    ("OpenFPS.Native", "OpenFPS.Native", Array.Empty<string>(), Array.Empty<string>()),
    ("OpenFPS.Common", "OpenFPS.Common", Array.Empty<string>(), Array.Empty<string>()),
    ("OpenFPS.Client.Core", "OpenFPS.Client.Core", new[] { "OpenFPS.Common", "OpenFPS.Native" }, Array.Empty<string>()),
    ("OpenFPS.Server", "OpenFPS.Server", new[] { "OpenFPS.Common" }, Array.Empty<string>()),
    ("OpenFPS.Client", "OpenFPS.Client", new[] { "OpenFPS.Common", "OpenFPS.Native", "OpenFPS.Client.Core" }, Array.Empty<string>()),
    ("OpenFPS.Client.Gtk", "OpenFPS.Client.Gtk", new[] { "OpenFPS.Common", "OpenFPS.Native", "OpenFPS.Client.Core" }, Array.Empty<string>()),
    ("OpenFPS.AudioLab", "OpenFPS.AudioLab", new[] { "OpenFPS.Common", "OpenFPS.Native", "OpenFPS.Client.Core", "OpenFPS.Server" }, Array.Empty<string>()),
    ("OpenFPS.Tests", "OpenFPS.Tests", new[] { "OpenFPS.Common", "OpenFPS.Native", "OpenFPS.Client.Core", "OpenFPS.Server" },
        new[] { "OpenFPS.Client.Gtk/Game/GtkKeyMap.cs" }),
};

var parse = new CSharpParseOptions(LanguageVersion.Preview);
var compilations = new Dictionary<string, CSharpCompilation>();
var ownTrees = new Dictionary<string, List<SyntaxTree>>();

foreach (var (name, dir, refs, extra) in projects)
{
    var files = Directory.EnumerateFiles(Path.Combine(repo, dir), "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .Concat(extra.Select(e => Path.Combine(repo, e)))
        .OrderBy(f => f, StringComparer.Ordinal)
        .ToList();
    var trees = files.Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, Rel(f), Encoding.UTF8)).ToList();
    ownTrees[name] = trees;
    var all = new List<SyntaxTree>(trees) { CSharpSyntaxTree.ParseText(implicitUsings, parse, $"<{name}>/GlobalUsings.g.cs") };
    if (name == "OpenFPS.Common")
        all.Add(CSharpSyntaxTree.ParseText(
            "namespace OpenFPS.Common; public static class WireContract { public const string Hash = \"survey\"; }\n" +
            "public static class DoorModelFingerprint { public const string Hash = \"survey\"; }", parse, "<OpenFPS.Common>/Generated.g.cs"));
    // The InternalsVisibleTo items of each project file.
    string[] friends = name switch
    {
        "OpenFPS.Native" => new[] { "OpenFPS.Client.Core", "OpenFPS.Tests", "OpenFPS.AudioLab" },
        "OpenFPS.Common" => new[] { "OpenFPS.Tests" },
        "OpenFPS.Client.Core" or "OpenFPS.Server" => new[] { "OpenFPS.Tests", "OpenFPS.AudioLab" },
        _ => Array.Empty<string>(),
    };
    if (friends.Length > 0)
        all.Add(CSharpSyntaxTree.ParseText(string.Join("\n", friends.Select(f =>
            $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{f}\")]")), parse, $"<{name}>/AssemblyInfo.g.cs"));
    var refList = new List<MetadataReference>(baseRefs);
    foreach (var r in refs) refList.Add(compilations[r].ToMetadataReference());
    var comp = CSharpCompilation.Create(name, all, refList,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
            nullableContextOptions: NullableContextOptions.Enable));
    if (Environment.GetEnvironmentVariable("SURVEY_GENERATORS") == "1") comp = RunGenerators(comp);
    compilations[name] = comp;
    int errors = comp.GetDiagnostics().Count(d => d.Severity == DiagnosticSeverity.Error);
    Console.Error.WriteLine($"{name}: {trees.Count} files, {errors} binding errors");
    if (Environment.GetEnvironmentVariable("SURVEY_ERRORS") == "1")
        foreach (var g in comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
                     .GroupBy(d => d.Id).OrderByDescending(g => g.Count()).Take(6))
            Console.Error.WriteLine($"  {g.Key} x{g.Count()}: {g.First().GetMessage()} ({g.First().Location.GetLineSpan()})");
}

// ── Types ───────────────────────────────────────────────────────────────────────────────────────
var typeRows = new List<string> { "type\tkind\tproject\tfile\tline\tlines\tnamespace\taccess\tstatic\tfiles" };
var declared = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
foreach (var (name, _, _, _) in projects)
{
    var comp = compilations[name];
    foreach (var tree in ownTrees[name])
    {
        var model = comp.GetSemanticModel(tree);
        foreach (var decl in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
                     .Where(d => d is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)
                     .Where(d => !d.Ancestors().Any(a => a is BaseTypeDeclarationSyntax)))
        {
            if (model.GetDeclaredSymbol(decl) is not INamedTypeSymbol t) continue;
            if (!declared.Add(t)) continue; // partial: the first file wins, the rest are listed
            var refsFiles = t.DeclaringSyntaxReferences.Select(r => r.SyntaxTree.FilePath).Distinct().ToList();
            int lines = t.DeclaringSyntaxReferences.Sum(r =>
            {
                var span = r.SyntaxTree.GetLineSpan(r.Span);
                return span.EndLinePosition.Line - span.StartLinePosition.Line + 1;
            });
            var first = t.DeclaringSyntaxReferences[0];
            typeRows.Add(string.Join('\t', Key(t), Kind(t), name, first.SyntaxTree.FilePath,
                first.SyntaxTree.GetLineSpan(first.Span).StartLinePosition.Line + 1, lines,
                t.ContainingNamespace.ToDisplayString(), t.DeclaredAccessibility, t.IsStatic,
                string.Join(';', refsFiles)));
        }
    }
}
File.WriteAllLines(Path.Combine(outDir, "types.tsv"), typeRows);

// ── Statics ─────────────────────────────────────────────────────────────────────────────────────
var statics = new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);
var staticRows = new List<string> { "symbol\towner\tproject\tfile\tline\tmutability\ttype\tthreadstatic\taccess" };
foreach (var t in declared.SelectMany(AllNested))
{
    string project = ProjectOf(t);
    foreach (var m in t.GetMembers())
    {
        if (m.IsImplicitlyDeclared || !m.IsStatic) continue;
        string? mut = null; ITypeSymbol? type = null;
        if (m is IFieldSymbol f && !f.IsConst)
        {
            type = f.Type;
            mut = f.IsReadOnly ? ContentsMutability(f.Type) : "assignable";
        }
        else if (m is IPropertySymbol p && !p.IsIndexer)
        {
            type = p.Type;
            bool auto = p.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>()
                .Any(s => s.AccessorList != null && s.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null));
            if (p.SetMethod != null && !p.SetMethod.IsInitOnly)
                mut = auto ? "assignable" : "settable (computed)";
            else if (auto)
                mut = ContentsMutability(p.Type);
        }
        if (mut == null) continue;
        bool threadStatic = m.GetAttributes().Any(a => a.AttributeClass?.Name == "ThreadStaticAttribute");
        var loc = m.Locations.FirstOrDefault(l => l.IsInSource);
        if (loc == null) continue;
        var ls = loc.GetLineSpan();
        string id = $"{Key(t)}.{m.Name}";
        statics[m.OriginalDefinition] = id;
        staticRows.Add(string.Join('\t', id, Key(Outer(t)), project, ls.Path, ls.StartLinePosition.Line + 1, mut,
            type!.ToDisplayString(), threadStatic, m.DeclaredAccessibility));
    }
}
File.WriteAllLines(Path.Combine(outDir, "statics.tsv"), staticRows);

// ── Edges, static references, assets ───────────────────────────────────────────────────────────
var edgeRows = new List<string> { "from_type\tfrom_project\tfile\tline\tto_type\tto_project\tmember\tsymbol_kind\tfrom_member" };
var memberRows = new List<string> { "type\tmember\tfile\tstart\tend" };
var refRows = new List<string> { "symbol\taccess\tfrom_type\tfrom_project\tfile\tline\tmember_context" };
var assetRows = new List<string> { "project\tfile\tline\tfrom_type\tkind\tsnippet" };
string[] assetExtensions = { ".json", ".wav", ".ogg", ".flac", ".sofa", ".bank", ".bin", ".csv", ".mp3", ".raw", ".ir", ".pcm", ".cache", ".txt" };

foreach (var (name, _, _, _) in projects)
{
    var comp = compilations[name];
    foreach (var tree in ownTrees[name])
    {
        var model = comp.GetSemanticModel(tree);
        var root = tree.GetRoot();
        foreach (var md in root.DescendantNodes().OfType<MemberDeclarationSyntax>()
                     .Where(m => m is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax or EventFieldDeclarationSyntax or IndexerDeclarationSyntax))
        {
            var span = md.GetLocation().GetLineSpan();
            memberRows.Add(string.Join('\t', Enclosing(model, md), MemberName(md), span.Path,
                span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1));
        }
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case SimpleNameSyntax sn:
                    HandleName(model, sn, name);
                    break;
                case ImplicitObjectCreationExpressionSyntax ioc:
                    if (model.GetSymbolInfo(ioc).Symbol is IMethodSymbol ctor) AddEdge(model, ioc, ctor.ContainingType, ".ctor", "Method", name);
                    break;
                case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                {
                    string v = lit.Token.ValueText;
                    if (assetExtensions.Any(e => v.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                        || v.Contains("assets", StringComparison.OrdinalIgnoreCase)
                        || v.StartsWith("Data/", StringComparison.Ordinal) || v.Contains("Sounds/", StringComparison.Ordinal))
                        AddAsset(name, lit, "path literal");
                    break;
                }
                case InterpolatedStringExpressionSyntax istr:
                {
                    string v = istr.ToString();
                    if (assetExtensions.Any(e => v.Contains(e + "\"", StringComparison.OrdinalIgnoreCase)))
                        AddAsset(name, istr, "path literal");
                    break;
                }
            }
        }

        void HandleName(SemanticModel model, SimpleNameSyntax sn, string project)
        {
            var info = model.GetSymbolInfo(sn);
            var sym = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (sym == null) return;
            if (sym is IAliasSymbol alias) sym = alias.Target;
            INamedTypeSymbol? target = sym switch
            {
                INamedTypeSymbol nt => nt,
                IMethodSymbol ms => (ms.ReducedFrom ?? ms).ContainingType,
                IFieldSymbol fs => fs.ContainingType,
                IPropertySymbol ps => ps.ContainingType,
                IEventSymbol es => es.ContainingType,
                _ => null,
            };
            if (target != null) AddEdge(model, sn, target, sym.Name, sym.Kind.ToString(), project);

            // Static state.
            var def = sym.OriginalDefinition;
            if ((def is IFieldSymbol || def is IPropertySymbol) && statics.TryGetValue(def, out var id))
            {
                var ls = sn.GetLocation().GetLineSpan();
                var from = Enclosing(model, sn);
                string ctx = sn.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() switch
                {
                    MethodDeclarationSyntax md => md.Identifier.Text,
                    ConstructorDeclarationSyntax cd => cd.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor",
                    PropertyDeclarationSyntax pd => pd.Identifier.Text,
                    FieldDeclarationSyntax fd => "init:" + fd.Declaration.Variables.First().Identifier.Text,
                    _ => "",
                };
                refRows.Add(string.Join('\t', id, AccessOf(sn), from, project, ls.Path, ls.StartLinePosition.Line + 1, ctx));
            }

            // File system and base directory.
            if (target != null)
            {
                string tn = target.ToDisplayString();
                string? kind = null;
                if (tn == "System.AppContext" && sym.Name == "BaseDirectory") kind = "AppContext.BaseDirectory";
                else if (tn == "System.AppDomain" && sym.Name == "BaseDirectory") kind = "AppDomain.BaseDirectory";
                else if (tn == "System.Environment" && sym.Name is "GetFolderPath" or "CurrentDirectory" or "GetEnvironmentVariable" or "ProcessPath")
                    kind = "Environment." + sym.Name;
                else if (tn == "System.Reflection.Assembly" && sym.Name is "Location" or "GetManifestResourceStream" or "GetManifestResourceNames")
                    kind = "Assembly." + sym.Name;
                else if (tn is "System.IO.File" or "System.IO.Directory" && sym is IMethodSymbol)
                    kind = tn.Substring(10) + "." + sym.Name;
                else if (tn is "System.IO.FileStream" or "System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.FileSystemWatcher"
                         && sym is IMethodSymbol { MethodKind: MethodKind.Constructor })
                    kind = "new " + tn.Substring(10);
                else if (tn is "System.IO.StreamReader" or "System.IO.StreamWriter" && sym is IMethodSymbol { MethodKind: MethodKind.Constructor } c
                         && c.Parameters.FirstOrDefault()?.Type.SpecialType == SpecialType.System_String)
                    kind = "new " + tn.Substring(10) + "(path)";
                else if (tn == "System.IO.Path" && sym.Name is "GetTempPath" or "GetFullPath")
                    kind = "Path." + sym.Name;
                else if ((tn.StartsWith("FMOD.") && sym.Name is "createSound" or "createStream" or "loadPlugin" or "loadBankFile" or "setPluginPath")
                         || (sym is IMethodSymbol { IsExtern: true } && sym.Name.Contains("HRTF", StringComparison.OrdinalIgnoreCase)))
                    kind = "native load: " + sym.Name;
                if (kind != null) AddAsset(project, sn, kind);
            }
        }

        void AddEdge(SemanticModel model, SyntaxNode node, INamedTypeSymbol target, string member, string symKind, string project)
        {
            var outer = Outer((INamedTypeSymbol)target.OriginalDefinition);
            if (!declared.Contains(outer)) return;
            string from = Enclosing(model, node);
            string to = Key(outer);
            if (from == to) return;
            var ls = node.GetLocation().GetLineSpan();
            edgeRows.Add(string.Join('\t', from, project, ls.Path, ls.StartLinePosition.Line + 1, to, ProjectOf(outer), member, symKind, MemberOf(node)));
        }

        void AddAsset(string project, SyntaxNode node, string kind)
        {
            var ls = node.GetLocation().GetLineSpan();
            var stmt = node.AncestorsAndSelf().FirstOrDefault(a => a is StatementSyntax or MemberDeclarationSyntax) ?? node;
            string snippet = string.Join(' ', stmt.ToString().Split('\n').Select(s => s.Trim())).Replace('\t', ' ');
            if (snippet.Length > 200) snippet = snippet.Substring(0, 200) + "...";
            assetRows.Add(string.Join('\t', project, ls.Path, ls.StartLinePosition.Line + 1, Enclosing(compilations[project].GetSemanticModel(node.SyntaxTree), node), kind, snippet));
        }
    }
}
File.WriteAllLines(Path.Combine(outDir, "edges.tsv"), edgeRows);
File.WriteAllLines(Path.Combine(outDir, "members.tsv"), memberRows);
File.WriteAllLines(Path.Combine(outDir, "static_refs.tsv"), refRows);
File.WriteAllLines(Path.Combine(outDir, "assets.tsv"), assetRows);
Console.Error.WriteLine($"{typeRows.Count - 1} types, {edgeRows.Count - 1} edges, {staticRows.Count - 1} statics, {refRows.Count - 1} static refs, {assetRows.Count - 1} asset uses");
return 0;

// ── Helpers ─────────────────────────────────────────────────────────────────────────────────────
string Rel(string f) => Path.GetRelativePath(repo, f).Replace('\\', '/');

static INamedTypeSymbol Outer(INamedTypeSymbol t)
{
    while (t.ContainingType != null) t = t.ContainingType;
    return t;
}

static IEnumerable<INamedTypeSymbol> AllNested(INamedTypeSymbol t) =>
    new[] { t }.Concat(t.GetTypeMembers().SelectMany(AllNested));

static string Key(INamedTypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat
    .WithGenericsOptions(SymbolDisplayGenericsOptions.None));

static string Kind(INamedTypeSymbol t) => t.TypeKind switch
{
    TypeKind.Class => t.IsRecord ? "record" : t.IsStatic ? "static class" : "class",
    TypeKind.Struct => t.IsRecord ? "record struct" : "struct",
    _ => t.TypeKind.ToString().ToLowerInvariant(),
};

string ProjectOf(INamedTypeSymbol t)
{
    var path = t.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath ?? "";
    foreach (var (n, _, _, _) in projects)
        if (ownTrees[n].Any(tr => tr.FilePath == path)) return n;
    return "?";
}

static string MemberOf(SyntaxNode node)
{
    var m = node.AncestorsAndSelf().OfType<MemberDeclarationSyntax>()
        .FirstOrDefault(a => a is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or FieldDeclarationSyntax
                             or EventFieldDeclarationSyntax or IndexerDeclarationSyntax);
    return m == null ? "" : MemberName(m);
}

static string MemberName(MemberDeclarationSyntax m)
{
    string name = m switch
    {
        MethodDeclarationSyntax md => md.Identifier.Text,
        ConstructorDeclarationSyntax cd => cd.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor",
        DestructorDeclarationSyntax => "~",
        OperatorDeclarationSyntax od => "op_" + od.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax co => "op_" + co.Type,
        PropertyDeclarationSyntax pd => pd.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        FieldDeclarationSyntax fd => fd.Declaration.Variables.First().Identifier.Text,
        EventFieldDeclarationSyntax ef => ef.Declaration.Variables.First().Identifier.Text,
        _ => "?",
    };
    // A nested type's member is named through the nested type, so two Update methods stay apart.
    var nested = m.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Skip(1).Select(t => t.Identifier.Text);
    string prefix = string.Join('.', nested);
    int line = m.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    return (prefix.Length > 0 ? prefix + "." : "") + name + "@" + line;
}

string Enclosing(SemanticModel model, SyntaxNode node)
{
    var decl = node.AncestorsAndSelf().LastOrDefault(a => a is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax);
    if (decl != null && model.GetDeclaredSymbol(decl) is INamedTypeSymbol t) return Key(t);
    return "<top-level:" + node.SyntaxTree.FilePath + ">";
}

static string ContentsMutability(ITypeSymbol type)
{
    if (type is IArrayTypeSymbol) return "readonly array (contents mutable)";
    if (type.IsValueType || type.SpecialType == SpecialType.System_String || type.SpecialType == SpecialType.System_Object) return "immutable";
    string ns = type.ContainingNamespace?.ToDisplayString() ?? "";
    string n = type.Name;
    if (type.TypeKind == TypeKind.Delegate) return "immutable";
    if (ns.StartsWith("System.Collections.Immutable") || ns.StartsWith("System.Collections.Frozen") || n.StartsWith("IReadOnly")
        || n is "Type" or "Regex" or "Uri" or "Version" or "ReadOnlyCollection" or "Encoding" or "ILogger" or "JsonSerializerOptions"
        || type.TypeKind == TypeKind.Interface && n.StartsWith("IEnumerable"))
        return "immutable";
    if (n is "Lock") return "lock";
    return "readonly ref (contents mutable)";
}

static string AccessOf(SimpleNameSyntax sn)
{
    SyntaxNode e = sn;
    if (e.Parent is MemberAccessExpressionSyntax ma && ma.Name == sn) e = ma;
    else if (e.Parent is MemberBindingExpressionSyntax mb) e = mb;
    var p = e.Parent;
    switch (p)
    {
        case AssignmentExpressionSyntax a when a.Left == e: return "write";
        case PrefixUnaryExpressionSyntax u when u.IsKind(SyntaxKind.PreIncrementExpression) || u.IsKind(SyntaxKind.PreDecrementExpression): return "write";
        case PostfixUnaryExpressionSyntax u2 when u2.IsKind(SyntaxKind.PostIncrementExpression) || u2.IsKind(SyntaxKind.PostDecrementExpression): return "write";
        case ArgumentSyntax arg when arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword): return "write";
        case ElementAccessExpressionSyntax ea when ea.Expression == e && ea.Parent is AssignmentExpressionSyntax a2 && a2.Left == ea: return "mutate";
        case MemberAccessExpressionSyntax inner when inner.Expression == e && inner.Parent is InvocationExpressionSyntax
             && Mutating(inner.Name.Identifier.Text): return "mutate";
        case MemberAccessExpressionSyntax inner2 when inner2.Expression == e && inner2.Parent is AssignmentExpressionSyntax a3 && a3.Left == inner2: return "mutate";
    }
    return "read";

    static bool Mutating(string n) => n is "Add" or "AddRange" or "Remove" or "RemoveAt" or "RemoveAll" or "RemoveRange" or "Clear"
        or "TryAdd" or "TryRemove" or "TryUpdate" or "AddOrUpdate" or "GetOrAdd" or "Enqueue" or "Dequeue" or "TryDequeue" or "Push"
        or "Pop" or "TryPop" or "Insert" or "InsertRange" or "Set" or "Reset" or "Fill" or "Sort" or "Reverse" or "Next" or "NextDouble"
        or "NextSingle" or "NextBytes" or "Restart" or "Start" or "Stop" or "TrimExcess" or "EnsureCapacity" or "Release" or "Write"
        or "Store" or "Invalidate" or "Prewarm" or "Put" or "Load" or "Register";
}

IEnumerable<string> PackageDlls(string package)
{
    string dir = Path.Combine(nuget, package);
    if (!Directory.Exists(dir)) yield break;
    var version = Directory.GetDirectories(dir).Select(Path.GetFileName).OrderByDescending(v => v, Comparer<string?>.Create(CompareVersions)).First();
    foreach (var tfm in new[] { "net10.0", "net9.0", "net8.0", "net7.0", "net6.0", "netstandard2.1", "netstandard2.0", "netcoreapp3.1", "netstandard1.1", "netstandard1.0" })
    {
        string lib = Path.Combine(dir, version!, "lib", tfm);
        if (Directory.Exists(lib))
        {
            foreach (var dll in Directory.GetFiles(lib, "*.dll")) yield return dll;
            yield break;
        }
    }
}

static int CompareVersions(string? a, string? b)
{
    Version.TryParse((a ?? "0").Split('-')[0], out var va);
    Version.TryParse((b ?? "0").Split('-')[0], out var vb);
    return (va ?? new Version()).CompareTo(vb ?? new Version());
}

static bool IsManaged(string dll)
{
    try { AssemblyName.GetAssemblyName(dll); return true; }
    catch { return false; }
}

CSharpCompilation RunGenerators(CSharpCompilation comp)
{
    if (!comp.ReferencedAssemblyNames.Any(a => a.Name == "MemoryPack.Core")) return comp;
    string? gen = PackageAnalyzer("memorypack.generator");
    if (gen == null) return comp;
    try
    {
        var reference = new AnalyzerFileReference(gen, new Loader());
        var generators = reference.GetGenerators(LanguageNames.CSharp);
        var driver = CSharpGeneratorDriver.Create(generators.ToArray()).WithUpdatedParseOptions(parse);
        driver.RunGeneratorsAndUpdateCompilation(comp, out var updated, out _);
        return (CSharpCompilation)updated;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"generator skipped: {ex.Message}");
        return comp;
    }
}

string? PackageAnalyzer(string package)
{
    string dir = Path.Combine(nuget, package);
    if (!Directory.Exists(dir)) return null;
    return Directory.GetFiles(dir, "*.Generator.dll", SearchOption.AllDirectories).FirstOrDefault(f => f.Contains("/cs/"));
}

sealed class Loader : IAnalyzerAssemblyLoader
{
    public void AddDependencyLocation(string fullPath) { }
    public Assembly LoadFromPath(string fullPath) => Assembly.LoadFrom(fullPath);
}

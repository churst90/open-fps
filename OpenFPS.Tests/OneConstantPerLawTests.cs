using System.Text.RegularExpressions;
using OpenFPS.Common;

namespace OpenFPS.Tests;

/// <summary>
/// One constant for each law (docs/PHYSICS_BOUNDARY.md, stage 2, 16.6): the number is written once, in
/// its home, and everything else reads it there. A second copy drifts: gravity was written 44 times, 40
/// of them 9.81 and 4 of them 9.80665, so a car's tyres and its brakes fell at different rates.
/// </summary>
public class OneConstantPerLawTests
{
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    /// <summary>The code of every project but the tests and FMOD's own bindings: each line with its
    /// comments taken off, by repository-relative path and line number.</summary>
    private static IEnumerable<(string File, int Line, string Code)> CodeLines()
    {
        string root = RepoRoot();
        foreach (var dir in Directory.EnumerateDirectories(root, "OpenFPS.*"))
        {
            string project = Path.GetFileName(dir);
            if (project is "OpenFPS.Tests" or "OpenFPS.Native") continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
                string inProject = rel.Substring(project.Length + 1);
                if (inProject.StartsWith("obj/") || inProject.StartsWith("bin/")) continue;
                int n = 0;
                foreach (var line in File.ReadLines(f))
                {
                    n++;
                    string code = line.TrimStart();
                    if (code.StartsWith("//") || code.StartsWith("*") || code.StartsWith("/*")) continue;
                    int comment = code.IndexOf("//", StringComparison.Ordinal);
                    yield return (rel, n, comment >= 0 ? code[..comment] : code);
                }
            }
        }
    }

    /// <summary>9.8, 9.81, 9.807 or 9.80665 as a number, in any type.</summary>
    private static readonly Regex GravityLiteral = new(@"(?<![\w.])9\.8(?:0665|07|1)?[fFdDmM]?(?![\w.])", RegexOptions.Compiled);

    /// <summary>A 9.8 given to a member whose name says it is something else (a cam's lift in mm, an
    /// engine's friction per thousand rpm).</summary>
    private static readonly Regex OtherQuantity = new(@"\b\w+(?:Mm|Krpm)\s*=\s*9\.8f", RegexOptions.Compiled);

    internal static List<string> GravityCopies()
        => CodeLines()
            .Where(l => GravityLiteral.IsMatch(OtherQuantity.Replace(l.Code, "")))
            .Where(l => l.File != "OpenFPS.Physics/Earth.cs")
            .Select(l => $"{l.File}:{l.Line}: {l.Code.Trim()}")
            .ToList();

    [Fact]
    public void Gravity_is_written_once()
    {
        var copies = GravityCopies();
        Assert.True(copies.Count == 0,
            "Gravity is Earth.StandardGravity (or a map's own); these write it again:\n" + string.Join("\n", copies));
        Assert.Equal(9.80665f, Earth.StandardGravity);
        Assert.Single(CodeLines(), l => l.File == "OpenFPS.Physics/Earth.cs" && GravityLiteral.IsMatch(l.Code));
    }
}

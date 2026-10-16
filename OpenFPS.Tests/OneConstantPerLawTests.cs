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

    /// <summary>How air's density was written beside the laws that use it: a density named and given
    /// 1.15 to 1.29; that times 343, the speed it goes with; half of it times a speed squared; rho c as
    /// 410 to 419; the standard day's 1.225 or 20 °C's 1.204 anywhere; 1.2 times a law with 343 or 293 K
    /// in it.</summary>
    private static readonly Regex[] DensityLiterals =
    {
        new(@"(?i)\b\w*(?:rho|density)\w*\s*=\s*1\.(?:1[5-9]|2)\d*[fFdDmM]?(?![\w.])", RegexOptions.Compiled),
        new(@"(?<![\w.])1\.(?:1[5-9]|2)\d*[fFdD]?\s*\*\s*343|343[fFdD]?\s*\*\s*1\.(?:1[5-9]|2)\d*[fFdD]?(?![\w.])", RegexOptions.Compiled),
        new(@"(?<![\w.])0\.5[fFdD]?\s*\*\s*1\.(?:1[5-9]|2)\d*[fFdD]?\s*\*", RegexOptions.Compiled),
        new(@"(?<![\w.])41[0-9](?:\.\d+)?[fFdD]?\s*[/*]|\*\s*41[0-9](?:\.\d+)?[fFdD]?(?![\w.])", RegexOptions.Compiled),
        new(@"(?i)\b\w*(?:rhoc|impedance)\w*\s*=\s*4[01]\d", RegexOptions.Compiled),
        new(@"(?i)(?=.*\bair).*(?<![\w.])1\.2(?:25|0[0-9]+|1)[fFdDmM]?(?![\w.])", RegexOptions.Compiled),
        new(@"(?<![\w.])1\.2(?:25|04|05)\d*[fFdDmM]?(?![\w.])", RegexOptions.Compiled),
        new(@"(?<![\w.])1\.2[01]?[fFdD]?\s*\*.*\b(?:343|293)", RegexOptions.Compiled),
    };

    internal static List<string> DensityCopies()
        => CodeLines()
            .Where(l => l.File != "OpenFPS.Materials/Air.cs")
            // A table of measured statistics is numbers, not a law.
            .Where(l => !l.Code.Contains("\", new[]", StringComparison.Ordinal))
            .Where(l => DensityLiterals.Any(r => r.IsMatch(l.Code)))
            .Select(l => $"{l.File}:{l.Line}: {l.Code.Trim()}")
            .ToList();

    [Fact]
    public void Air_density_is_one_law()
    {
        var copies = DensityCopies();
        Assert.True(copies.Count == 0,
            "Air's density is Air.Density, from its temperature and pressure (Air.Reference for the heard laws, Air.Standard "
            + "or the map's air for drag); these write it again:\n" + string.Join("\n", copies));
        Assert.Equal(1.225f, Air.Standard.Density, 3);
        Assert.Equal(1.204f, Air.Reference.Density, 3);
        // The heard laws' air is the one their speed of sound is: 343 m/s at 20 °C.
        Assert.Equal(AcousticConstants.SpeedOfSound, Air.Reference.SpeedOfSound, 0);
    }

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

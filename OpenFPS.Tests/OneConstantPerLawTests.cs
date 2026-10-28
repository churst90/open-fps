using System.Text.RegularExpressions;
using OpenFPS.Physics;
using OpenFPS.Acoustics;
using OpenFPS.Materials;

namespace OpenFPS.Tests;

/// <summary>
/// One constant for each law (docs/PHYSICS_BOUNDARY.md, stage 2, 16.6 to 16.8): the number is written once, in
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

    /// <summary>A time parameter: a double or float named seconds, time, t, clock or now, or ending in
    /// Seconds, Time or Clock.</summary>
    private const string TimeParameter = @"\b(?:double|float)\s+(?:seconds|time|t|clock|now|\w+(?:Seconds|Time|Clock))\b";

    /// <summary>How a second gust law was written: a method taking a gustiness and a clock and returning the
    /// wind swung by it (WindModel.Felt), a gust-named method of a clock (WindModel.GustFactor), or a sine
    /// or cosine weighed by the gustiness on one line.</summary>
    private static readonly Regex[] GustLaws =
    {
        new(@"^(?:\w+\s+)*[\w<>\[\],.?]+\s+\w+\s*\((?=[^)]*\b(?:float|double)\s+\w*[Gg]ust\w*)(?=[^)]*" + TimeParameter + ")", RegexOptions.Compiled),
        new(@"^(?:\w+\s+)*[\w<>\[\],.?]+\s+\w*Gust\w*\s*\((?=[^)]*" + TimeParameter + ")", RegexOptions.Compiled),
        new(@"(?i)gust.*\bMathF?\.(?:Sin|Cos)\(|\bMathF?\.(?:Sin|Cos)\(.*gust", RegexOptions.Compiled),
    };

    internal static List<string> GustLawCopies()
        => CodeLines()
            .Where(l => l.File != "OpenFPS.Physics/Wind.cs")
            .Where(l => GustLaws.Any(r => r.IsMatch(l.Code)))
            .Select(l => $"{l.File}:{l.Line}: {l.Code.Trim()}")
            .ToList();

    [Fact]
    public void Wind_is_one_gust_law()
    {
        // Bullets felt WindModel's three sines on a clock while the ears heard WindField's eddies: an 8 m/s
        // crosswind moved a 300 m shot 0.44 m on the one and 0.25 m on the other (PHYSICS_BOUNDARY 16.7).
        var copies = GustLawCopies();
        Assert.True(copies.Count == 0,
            "The wind's gusts are WindField's (AirSample.Of, or WindField.VelocityAt with the broadcast weather); these "
            + "write another gust law:\n" + string.Join("\n", copies));
        // The home is found by the same patterns: the guard is not blind to the law it protects.
        Assert.Contains(File.ReadLines(Path.Combine(RepoRoot(), "OpenFPS.Physics", "Wind.cs")),
                        l => GustLaws[1].IsMatch(l.TrimStart()));
    }

    /// <summary>The road load's two forms (16.8): a drag area, or a rolling resistance, in a product.</summary>
    private static readonly Regex[] RoadLoadForms =
    {
        new(@"(?i)\w*DragArea\w*\s*\*|\*\s*[\w.?]*DragArea\b", RegexOptions.Compiled),
        new(@"(?i)\w*RollingResistance\w*\s*\*|\*\s*[\w.?]*RollingResistance\b", RegexOptions.Compiled),
    };

    internal static List<string> RoadLoadCopies()
        => CodeLines()
            .Where(l => l.File != "OpenFPS.Physics/RoadLoad.cs")
            .Where(l => RoadLoadForms.Any(r => r.IsMatch(l.Code)))
            .Select(l => $"{l.File}:{l.Line}: {l.Code.Trim()}")
            .ToList();

    /// <summary>The capability (16.8): a tyre's peak grip times a gravity.</summary>
    private static readonly Regex CapabilityForm =
        new(@"(?=.*\bPeakGripG\b)(?=.*(?:Gravity|gravity|\bg\b)).*\*", RegexOptions.Compiled);

    internal static List<string> CapabilityCopies()
        => CodeLines()
            .Where(l => l.File != "OpenFPS.Physics/Brakes.cs")
            .Where(l => CapabilityForm.IsMatch(l.Code))
            .Select(l => $"{l.File}:{l.Line}: {l.Code.Trim()}")
            .ToList();

    [Fact]
    public void Road_load_is_written_once()
    {
        var copies = RoadLoadCopies();
        Assert.True(copies.Count == 0,
            "Drag and rolling resistance are RoadLoad.Drag and RoadLoad.Rolling; these write them again:\n" + string.Join("\n", copies));
    }

    [Fact]
    public void Braking_capability_is_written_once()
    {
        var copies = CapabilityCopies();
        Assert.True(copies.Count == 0,
            "What a car's tyres can slow it at is Brakes.Capacity; these write it again:\n" + string.Join("\n", copies));
    }

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

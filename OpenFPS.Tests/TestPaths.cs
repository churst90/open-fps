using System.Runtime.CompilerServices;

namespace OpenFPS.Tests;

/// <summary>Where the checkout's sound bank is, on any machine and wherever the build output went: OPENFPS_SOUNDS
/// if set, else beside the build output's parents, else from this file's own path as the compiler saw it.</summary>
internal static class TestPaths
{
    public static string Sounds([CallerFilePath] string here = "")
    {
        string? set = Environment.GetEnvironmentVariable("OPENFPS_SOUNDS");
        if (!string.IsNullOrEmpty(set) && Directory.Exists(set)) return set;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS"))) dir = dir.Parent;
        if (dir != null) return Path.Combine(dir.FullName, "OpenFPS.Client", "ASSETS", "SOUNDS");
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "OpenFPS.Client", "ASSETS", "SOUNDS"));
    }
}

using System;
using System.IO;

namespace OpenFPS.AudioLab;

/// <summary>
/// Where the lab finds the repository, and where it writes what it renders. One place, so every
/// instrument finds the maps, the recordings and the client's sounds the same way.
/// </summary>
internal static class LabPaths
{
    /// <summary>Used when neither the working directory nor the lab's own folder is inside a checkout,
    /// as when the lab is built off the repository's volume and run from there.</summary>
    private const string Checkout = "/home/cody/external-rescue/Github/open-fps";

    private static string? _repo;

    /// <summary>The repository: the first folder holding OpenFPS.slnx, walking up from the working
    /// directory, then from the lab's own folder; failing both, <see cref="Checkout"/>. Not a folder
    /// named OpenFPS.Server: the build output has one of those too.</summary>
    public static string Repo => _repo ??= Up(Directory.GetCurrentDirectory()) ?? Up(AppContext.BaseDirectory) ?? Checkout;

    /// <summary>A path in the repository.</summary>
    public static string InRepo(params string[] parts) => Path.Combine([Repo, .. parts]);

    /// <summary>A path under OpenFPS.Server: maps, prefabs, machines.</summary>
    public static string Server(params string[] parts) => Path.Combine([Repo, "OpenFPS.Server", .. parts]);

    /// <summary>A path under the client's sounds, OpenFPS.Client/ASSETS/SOUNDS.</summary>
    public static string Sounds(params string[] parts) => Path.Combine([Repo, "OpenFPS.Client", "ASSETS", "SOUNDS", .. parts]);

    /// <summary>A folder for renders, ASSETS/SOUNDS/... beside the lab's binary. Not created.</summary>
    public static string Output(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "ASSETS", "SOUNDS", .. parts]);

    /// <summary>The path if it exists, as a file or a folder; otherwise null.</summary>
    public static string? Existing(string path) => File.Exists(path) || Directory.Exists(path) ? path : null;

    private static string? Up(string from)
    {
        for (var dir = new DirectoryInfo(from); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OpenFPS.slnx"))) return dir.FullName;
        return null;
    }
}

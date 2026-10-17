using System.Runtime.CompilerServices;
using OpenFPS.Client.Core;

namespace OpenFPS.Tests;

/// <summary>
/// No test reads or writes the player's own settings or data: each test process gets scratch folders before
/// any test runs, removed at exit. A test that switched a beacon category off would otherwise switch it off
/// for the player once anything made preferences save to disk. The settings folder and the two caches are
/// moved by the client's own variables, which work on every system; $XDG_CONFIG_HOME and $XDG_DATA_HOME are
/// moved too, for anything that asks the platform itself where Linux lets a process say.
/// </summary>
internal static class TestConfigIsolation
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "openfps-test-config-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", scratch);
        // A folder of its own, not $XDG_CONFIG_HOME/openfps: on Linux that is the platform's folder in a test,
        // and a test that unsets the variable must see the folder change.
        Environment.SetEnvironmentVariable(SettingsFolder.Variable, Path.Combine(scratch, "settings"));
        // And the player's data: the door render cache (ClientAudioPaths) prunes other builds' folders beside its own.
        string data = Path.Combine(scratch, "data");
        Directory.CreateDirectory(data);
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
        Environment.SetEnvironmentVariable("OPENFPS_RENDER_CACHE", Path.Combine(data, "renders"));
        Environment.SetEnvironmentVariable("OPENFPS_MESH_CACHE", Path.Combine(data, "meshes"));
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }
}

public class TestConfigIsolationTests
{
    private static string RepoRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, ".."));

    [Fact]
    public void TestsNeverSeeThePlayersSettingsFolder()
    {
        string own = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openfps");
        // The checks below mean something only if the temporary folder is not inside the player's own.
        Assert.False(Path.GetTempPath().StartsWith(own, StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith(Path.GetTempPath(), SettingsFolder.Path);
        Assert.StartsWith(Path.GetTempPath(), ClientSettings.DefaultPath);
        Assert.StartsWith(Path.GetTempPath(), BeaconPreferences.DefaultPath);
    }

    [Fact]
    public void TestsNeverWriteThePlayersRenderCache()
    {
        string? folder = ClientAudioPaths.ForPlayer().RenderCache;
        if (folder != null) Assert.StartsWith(Path.GetTempPath(), folder);
    }

    [Fact]
    public void TestsNeverWriteThePlayersMeshCache()
    {
        string? folder = MeshAssetFetcher.Folder;
        if (folder != null) Assert.StartsWith(Path.GetTempPath(), folder);
    }

    /// <summary>
    /// The three checks above cover every folder of the player's own only while these are the only places the
    /// game asks the system for one. A new one goes here with what keeps the tests out of it.
    /// </summary>
    [Fact]
    public void ThePlayersFoldersAreAskedForInTheKnownPlacesOnly()
    {
        var known = new SortedSet<string>(StringComparer.Ordinal)
        {
            // Moved by OPENFPS_SETTINGS_DIR.
            "OpenFPS.Client.Core/SettingsFolder.cs: SpecialFolder.ApplicationData",
            // Moved by OPENFPS_RENDER_CACHE and OPENFPS_MESH_CACHE.
            "OpenFPS.Client.Core/ClientAudioPaths.cs: SpecialFolder.LocalApplicationData",
            "OpenFPS.Client.Core/MeshAssetFetcher.cs: SpecialFolder.LocalApplicationData",
            // The two programs' own start-up (a log folder, a crash folder): no test runs a program's Main.
            "OpenFPS.Client/Program.cs: SpecialFolder.LocalApplicationData",
            "OpenFPS.Client.Gtk/Program.cs: SpecialFolder.UserProfile",
        };
        var asks = new[]
        {
            "SpecialFolder.ApplicationData", "SpecialFolder.LocalApplicationData", "SpecialFolder.CommonApplicationData",
            "SpecialFolder.UserProfile", "SpecialFolder.Personal", "SpecialFolder.MyDocuments", "SpecialFolder.Desktop",
            "\"XDG_", "\"APPDATA\"", "\"LOCALAPPDATA\"", "\"USERPROFILE\"", "\"HOME\"",
        };
        string root = RepoRoot();
        var found = new SortedSet<string>(StringComparer.Ordinal);
        int files = 0;
        foreach (string project in Directory.GetDirectories(root, "OpenFPS.*"))
        {
            string name = Path.GetFileName(project);
            // The tests are what is being kept out; the lab is run by hand and writes where it is told.
            if (name is "OpenFPS.Tests" or "OpenFPS.AudioLab") continue;
            foreach (string path in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                files++;
                foreach (string code in File.ReadLines(path))
                {
                    if (code.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                    foreach (string ask in asks)
                        if (code.Contains(ask, StringComparison.Ordinal)) found.Add($"{rel}: {ask.Trim('"')}");
                }
            }
        }
        Assert.True(files > 500, $"only {files} source files found under {root}");
        Assert.Equal(string.Join("\n", known), string.Join("\n", found));
    }
}

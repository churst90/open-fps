using System.Runtime.CompilerServices;

namespace OpenFPS.Tests;

/// <summary>
/// No test writes the player's own settings or data: each test process gets scratch $XDG_CONFIG_HOME and
/// $XDG_DATA_HOME folders before any test runs, removed at exit. A test that switched a beacon category off
/// would otherwise switch it off for the player once anything made preferences save to disk.
/// </summary>
internal static class TestConfigIsolation
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "openfps-test-config-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", scratch);
        // And the player's data: DoorRenderCache lives under LocalApplicationData and prunes other builds' folders.
        Directory.CreateDirectory(Path.Combine(scratch, "data"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(scratch, "data"));
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
    [Fact]
    public void TestsNeverSeeThePlayersConfigFolder()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.StartsWith(Path.GetTempPath(), appData);
        Assert.NotEqual(Path.Combine(home, ".config"), appData);
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Assert.StartsWith(Path.GetTempPath(), localData);
    }

    [Fact]
    public void TestsNeverWriteThePlayersRenderCache()
    {
        string? folder = OpenFPS.Client.AudioEngine.Core.DoorRenderCache.Folder;
        if (folder != null) Assert.StartsWith(Path.GetTempPath(), folder);
    }
}

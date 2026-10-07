using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// No test may write the player's own settings. BeaconPreferences and ClientSettings live under
/// $XDG_CONFIG_HOME (or ~/.config), and a test that switches a beacon category off would otherwise
/// switch it off for the player too, the moment anything (a mutant, a refactor) made preferences save
/// to disk. Every test process starts with a scratch config folder of its own, before any test runs,
/// and removes it when the process exits.
/// </summary>
internal static class TestConfigIsolation
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "openfps-test-config-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", scratch);
        // And the player's data: every ClientAudioSystem a test builds renders the city's doors in the
        // background and keeps them in the render cache (DoorRenderCache, under LocalApplicationData),
        // pruning the folders of other builds. Without this that was the player's own cache.
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

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Xunit;

namespace OpenFPS.Tests;

/// <summary>
/// No test may write the player's own settings. BeaconPreferences and ClientSettings live under
/// $XDG_CONFIG_HOME (or ~/.config), and on 2026-09-24 a mutation-testing run in the real home turned
/// a player's door and vehicle beacons off: a mutant made in-memory preferences save to disk, and the
/// two tests that switch those categories off wrote exactly that. Every test process now starts with
/// a scratch config folder of its own, before any test runs.
/// </summary>
internal static class TestConfigIsolation
{
    [ModuleInitializer]
    internal static void Isolate()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "openfps-test-config-" + Environment.ProcessId);
        Directory.CreateDirectory(scratch);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", scratch);
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
    }
}

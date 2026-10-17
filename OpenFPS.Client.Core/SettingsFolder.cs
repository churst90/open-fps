namespace OpenFPS.Client.Core;

/// <summary>
/// The folder the client keeps the player's own choices in (client.json, beacons.json), decided here and
/// nowhere else: the platform's per-user config folder (%APPDATA%\openfps on Windows, $XDG_CONFIG_HOME/openfps
/// or ~/.config/openfps on Linux, ~/Library/Application Support/openfps on a Mac), or the folder
/// OPENFPS_SETTINGS_DIR names. Windows does not let a process move its application data, so the tests move
/// this instead (TestConfigIsolation), on every system.
/// </summary>
public static class SettingsFolder
{
    /// <summary>The environment variable that moves the folder. Read each time, so a run can move it.</summary>
    public const string Variable = "OPENFPS_SETTINGS_DIR";

    public static string Path =>
        System.Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } moved
            ? moved
            : System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "openfps");

    /// <summary>The full name of one of the player's files there.</summary>
    public static string File(string name) => System.IO.Path.Combine(Path, name);
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenFPS.Client.Core;

/// <summary>A server you have saved: where it is, and who you log in as there.</summary>
public sealed class SavedServer
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 33288;
    public string Username { get; set; } = "";
    /// <summary>Kept only if you asked for it to be; the file is readable by you alone.</summary>
    public string Password { get; set; } = "";
    public bool RememberPassword { get; set; }
    /// <summary>The one Connect goes to.</summary>
    public bool Preferred { get; set; }

    public override string ToString()
        => $"{(Name.Length > 0 ? Name : Host)}, {Host} port {Port}{(Username.Length > 0 ? $", as {Username}" : "")}{(Preferred ? ", preferred" : "")}";
}

/// <summary>
/// Everything the client remembers between runs: interface sounds, audio devices, saved servers.
/// One file for every client head, in the platform's per-user config folder (~/.config/openfps on
/// Linux, %APPDATA%\openfps on Windows, ~/Library/Application Support/openfps on a Mac).
/// </summary>
public sealed class ClientSettings
{
    public bool UiSounds { get; set; } = true;
    public float UiVolume { get; set; } = 0.5f;
    /// <summary>Output device by name; empty is the system default.</summary>
    public string OutputDevice { get; set; } = "";
    /// <summary>Microphone by name, for voice chat; empty is the system default.</summary>
    public string InputDevice { get; set; } = "";
    public List<SavedServer> Servers { get; set; } = new();

    public SavedServer? Preferred => Servers.FirstOrDefault(s => s.Preferred) ?? (Servers.Count == 1 ? Servers[0] : null);

    public void SetPreferred(SavedServer server)
    {
        foreach (var s in Servers) s.Preferred = ReferenceEquals(s, server);
    }

    // ── The file ───────────────────────────────────────────────────────────────────────────────

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "openfps", "client.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static ClientSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(path), Json) ?? new ClientSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning("Client settings at {Path} could not be read ({Error}); starting from defaults.", path, ex.Message);
        }
        return new ClientSettings();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        // A password is written only for a server that asked to remember it.
        foreach (var s in Servers) if (!s.RememberPassword) s.Password = "";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { }
    }
}

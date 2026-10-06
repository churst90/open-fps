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
    /// <summary>The sounds for somebody logging in, logging out, losing connection, going away and
    /// coming back. The notices themselves are spoken either way.</summary>
    public bool PresenceSounds { get; set; } = true;
    /// <summary>Output device by name; empty is the system default.</summary>
    public string OutputDevice { get; set; } = "";
    /// <summary>Microphone by name, for voice chat; empty is the system default.</summary>
    public string InputDevice { get; set; } = "";
    public List<SavedServer> Servers { get; set; } = new();

    /// <summary>
    /// How much of the real difference in loudness between sounds reaches the mix: 1 is real life,
    /// lower squeezes loud and quiet together (see Loudness.DynamicRangeCompression). Set with
    /// `/levels` in game. The live value is the truth: <see cref="Save"/> records it.
    /// </summary>
    public float LevelCompression { get; set; } = OpenFPS.Common.Loudness.DefaultCompression;

    /// <summary>
    /// How loud your headphones play the game: the level, dB SPL at your ears, of a sound the mix places
    /// at its pivot. 70 (the default) is the pivot at its own level, a normal voice at arm's length at a
    /// conversational 62 dB. Set by ear with `/listening`, or `/listening 65`. It changes no level in the
    /// mix, only how much of a sound's tone the ear model gives back at the level it plays at
    /// (docs/EAR_MODEL.md). The live value is the truth: <see cref="Save"/> records it.
    /// </summary>
    public float ListeningLevelDb { get; set; } = OpenFPS.Common.Hearing.EarModel.DefaultListeningLevelDb;

    /// <summary>Puts this file's listening level into play, unless the environment chose one for the
    /// run. Each head calls it once, after loading.</summary>
    public void ApplyHearing()
    {
        if (!OpenFPS.Common.Hearing.EarModel.ListeningFromEnvironment)
            OpenFPS.Common.Hearing.EarModel.ListeningLevelDb = ListeningLevelDb;
    }

    /// <summary>Saying what is in front of you as you turn (N in game). See <see cref="NavigationAids"/>.</summary>
    public bool TurnNarration { get; set; } = true;
    /// <summary>The knock and the name when you walk into something (/bumps in game).</summary>
    public bool WallBumps { get; set; } = true;
    /// <summary>Aim assistance for shots from the hip (/aimassist in game). The server does the assisting;
    /// the client tells it this each time it enters the world.</summary>
    public bool AimAssist { get; set; } = true;
    /// <summary>What comma and period step through: "Doors", "Items", "Places"... (Shift with either
    /// changes it, or /track). See <see cref="MapTracker"/>.</summary>
    public string TrackCategory { get; set; } = nameof(Core.TrackCategory.Doors);

    /// <summary>Puts this file's navigation aids into play. Each head calls it once, after loading.</summary>
    public void ApplyNavigationAids()
    {
        NavigationAids.TurnNarration = TurnNarration;
        NavigationAids.WallBumps = WallBumps;
        NavigationAids.AimAssist = AimAssist;
        NavigationAids.Track = MapTracker.Parse(TrackCategory) ?? Core.TrackCategory.Doors;
    }

    public SavedServer? Preferred => Servers.FirstOrDefault(s => s.Preferred) ?? (Servers.Count == 1 ? Servers[0] : null);

    public void SetPreferred(SavedServer server)
    {
        foreach (var s in Servers) s.Preferred = ReferenceEquals(s, server);
    }

    /// <summary>
    /// A server logged in to by hand is remembered, so Connect can go straight back: added if it is
    /// new (and preferred if it is the only one), its password kept only if asked. Saved. Each account is
    /// its own entry: a second account on a saved server is a second entry under the same name, and which
    /// one is preferred stays the player's choice.
    /// </summary>
    public void Remember(string address, string user, string pass, bool rememberPassword, string? path = null)
    {
        ServerAddress.Parse(address, out string host, out int port);
        var s = Servers.FirstOrDefault(x => x.Host == host && x.Port == port && x.Username == user);
        if (s == null)
        {
            string name = Servers.FirstOrDefault(x => x.Host == host && x.Port == port)?.Name ?? host;
            s = new SavedServer { Name = name.Length > 0 ? name : host, Host = host, Port = port, Username = user };
            Servers.Add(s);
            if (Servers.Count == 1) s.Preferred = true;
        }
        s.RememberPassword = rememberPassword;
        s.Password = rememberPassword ? pass : "";
        Save(path);
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
        // What is playing, unless a run's environment chose it: that is not the player's choice.
        if (!OpenFPS.Common.Loudness.CompressionFromEnvironment)
            LevelCompression = OpenFPS.Common.Loudness.DynamicRangeCompression;
        if (!OpenFPS.Common.Hearing.EarModel.ListeningFromEnvironment)
            ListeningLevelDb = OpenFPS.Common.Hearing.EarModel.ListeningLevelDb;
        // ...and the navigation aids as they are now: a key in game turns them, not this copy.
        TurnNarration = NavigationAids.TurnNarration;
        WallBumps = NavigationAids.WallBumps;
        AimAssist = NavigationAids.AimAssist;
        TrackCategory = NavigationAids.Track.ToString();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { }
    }
}

/// <summary>
/// The live switches for the two navigation aids, which the game reads and a key or a command flips.
/// Static, like Loudness.DynamicRangeCompression and for the same reason: a head holds its own copy of
/// <see cref="ClientSettings"/> for the life of the run, and the live value is what
/// <see cref="ClientSettings.Save"/> must write, whoever saves.
/// </summary>
public static class NavigationAids
{
    /// <summary>Say what is ahead once the heading settles after a turn.</summary>
    public static bool TurnNarration { get; set; } = true;
    /// <summary>A knock and the thing's name when you walk into it.</summary>
    public static bool WallBumps { get; set; } = true;
    /// <summary>Aim assistance for shots from the hip, which the server applies: see /aimassist.</summary>
    public static bool AimAssist { get; set; } = true;
    /// <summary>The kind of thing comma and period step through.</summary>
    public static TrackCategory Track { get; set; } = TrackCategory.Doors;
}

/// <summary>"host:port" as typed into a connect form or saved, with the defaults filled in.</summary>
public static class ServerAddress
{
    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 33288;

    public static void Parse(string address, out string host, out int port)
    {
        host = DefaultHost; port = DefaultPort;
        var parts = (address ?? "").Trim().Split(':');
        if (parts.Length >= 1 && parts[0].Length > 0) host = parts[0];
        if (parts.Length >= 2 && int.TryParse(parts[1], out int p)) port = p;
    }
}

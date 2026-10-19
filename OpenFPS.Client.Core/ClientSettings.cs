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
/// One file for every client head, client.json in the settings folder (<see cref="SettingsFolder"/>).
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
    /// How much of the real difference in loudness reaches the mix: 1 is real life, lower squeezes loud
    /// and quiet together (HearingSettings.Compression, `/levels`). <see cref="Save"/> records the
    /// live value.
    /// </summary>
    public float LevelCompression { get; set; } = OpenFPS.Sound.Loudness.DefaultCompression;

    /// <summary>
    /// How loud the headphones play: dB SPL at the ears of a normal voice at arm's length as the game
    /// plays it; 62.35, the default, is as loud as life. Set by ear with `/listening`. It changes no level
    /// in the mix, only how much tone the ear model gives back (docs/EAR_MODEL.md). <see cref="Save"/>
    /// records the live value.
    /// </summary>
    public float ListeningLevelDb { get; set; } = OpenFPS.Sound.Hearing.EarModel.DefaultListeningLevelDb;

    /// <summary>Puts this file's levels and listening level into play in <paramref name="hearing"/> (the audio
    /// world's, AudioEngineFacade.Hearing), each unless the environment chose it for the run, and remembers it:
    /// <see cref="Save"/> records what is live there. Each head calls it once, after loading.</summary>
    public void ApplyHearing(OpenFPS.Sound.Hearing.HearingSettings hearing)
    {
        _hearing = hearing;
        if (!OpenFPS.Sound.Loudness.CompressionFromEnvironment)
            hearing.Compression = LevelCompression;
        if (!OpenFPS.Sound.Hearing.EarModel.ListeningFromEnvironment)
            hearing.ListeningLevelDb = ListeningLevelDb;
    }

    /// <summary>The world's hearing this copy was applied to (<see cref="ApplyHearing"/>).</summary>
    private OpenFPS.Sound.Hearing.HearingSettings? _hearing;

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
    /// <summary>How far round you a large map is loaded: "low", "medium" or "high" (/detail in game).
    /// See <see cref="Core.WorldDetail"/>.</summary>
    public string WorldDetail { get; set; } = "medium";
    /// <summary>The driving sounds (see <see cref="DrivingCues"/>): all of them (Shift+K), and each one.</summary>
    public bool DriveCues { get; set; } = true;
    public bool DriveGuide { get; set; } = true;
    public bool DriveLineSensors { get; set; } = true;
    public bool DriveTurnClicks { get; set; } = true;
    public bool DriveBrakeCue { get; set; } = true;
    public bool DriveSpeedWarning { get; set; } = true;
    /// <summary>The world editor's direct keys (/editorkeys). Off: they are to be tried with Orca and NVDA
    /// before anybody has them on (docs/WORLD_EDITOR.md section 11.8).</summary>
    public bool EditorDirectKeys { get; set; }

    /// <summary>Puts this file's navigation aids into play. Each head calls it once, after loading.</summary>
    public void ApplyNavigationAids()
    {
        NavigationAids.TurnNarration = TurnNarration;
        EditorKeys.Enabled = EditorDirectKeys;
        NavigationAids.WallBumps = WallBumps;
        NavigationAids.AimAssist = AimAssist;
        NavigationAids.Track = MapTracker.Parse(TrackCategory) ?? Core.TrackCategory.Doors;
        DrivingCues.Enabled = DriveCues;
        DrivingCues.Guide = DriveGuide;
        DrivingCues.LineSensors = DriveLineSensors;
        DrivingCues.TurnClicks = DriveTurnClicks;
        DrivingCues.BrakeCue = DriveBrakeCue;
        DrivingCues.SpeedWarning = DriveSpeedWarning;
        Core.WorldDetail.Level = OpenFPS.Common.StreamRadii.Named(WorldDetail) != null ? WorldDetail.Trim().ToLowerInvariant() : "medium";
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

    public static string DefaultPath => SettingsFolder.File("client.json");

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

    /// <param name="path">The file; the player's settings file by default.</param>
    /// <param name="hearing">The world's hearing whose levels and listening level are live: the one this copy
    /// was applied to by default. With neither, this copy's own are written.</param>
    public void Save(string? path = null, OpenFPS.Sound.Hearing.HearingSettings? hearing = null)
    {
        hearing ??= _hearing;
        path ??= DefaultPath;
        // A password is written only for a server that asked to remember it.
        foreach (var s in Servers) if (!s.RememberPassword) s.Password = "";
        // What is playing, unless a run's environment chose it: that is not the player's choice.
        if (hearing != null && !OpenFPS.Sound.Loudness.CompressionFromEnvironment)
            LevelCompression = hearing.Compression;
        if (hearing != null && !OpenFPS.Sound.Hearing.EarModel.ListeningFromEnvironment)
            ListeningLevelDb = hearing.ListeningLevelDb;
        // ...and the navigation aids as they are now: a key in game turns them, not this copy.
        TurnNarration = NavigationAids.TurnNarration;
        EditorDirectKeys = EditorKeys.Enabled;
        WallBumps = NavigationAids.WallBumps;
        AimAssist = NavigationAids.AimAssist;
        TrackCategory = NavigationAids.Track.ToString();
        DriveCues = DrivingCues.Enabled;
        DriveGuide = DrivingCues.Guide;
        DriveLineSensors = DrivingCues.LineSensors;
        DriveTurnClicks = DrivingCues.TurnClicks;
        DriveBrakeCue = DrivingCues.BrakeCue;
        DriveSpeedWarning = DrivingCues.SpeedWarning;
        WorldDetail = Core.WorldDetail.Level;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { }
    }
}

/// <summary>
/// The live switches for the navigation aids, which a key or a command flips. Static: a head
/// keeps its own copy of <see cref="ClientSettings"/> for the run, and the live value is what
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

/// <summary>
/// How far round the player a map streamed in tiles is loaded (docs/WORLD_STREAMING.md): everything to
/// 150, 300 or 500 metres, and the ground, roads and building shells to 500, 800 or 1,200. Sent with each
/// map request, and to the server when /detail changes it. Static for the same reason as
/// <see cref="NavigationAids"/>.
/// </summary>
public static class WorldDetail
{
    /// <summary>"low", "medium" or "high".</summary>
    public static string Level { get; set; } = "medium";

    public static OpenFPS.Common.StreamRadii Radii => OpenFPS.Common.StreamRadii.Named(Level) ?? OpenFPS.Common.StreamRadii.Default;
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
